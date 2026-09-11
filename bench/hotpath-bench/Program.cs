// ClientKeyService.AuthorizeRequest 热路径基准（架构审查 §5 测量先行）。
// 度量维度：key 数 N × 并发 C × 后端（文件 / MariaDB），指标：p50/p95/p99 延迟、
// 分配量/操作（单线程）、吞吐（多线程）。DB 单线程延迟即全局锁内每次准入的
// 持锁时长 → 序列化吞吐上限 = 1000/p50(ms)。
//
// 用法：
//   dotnet run -c Release                                  # 仅文件后端
//   OPTIROUTER_MARIADB_TEST="<连接串>" dotnet run -c Release # 含 MariaDB 后端（自建/自删 scratch 库）
//
// 结论约束：单机开发环境测量，量级决策用，不冒充生产基准（审查报告同款口径）。

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

const string Plaintext = "opti-key-bench";
const int LatencyOps = 2000;
const int WarmupOps = 300;
const int ThroughputSeconds = 2;

Console.WriteLine($"# AuthorizeRequest hot path bench  ({DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}UTC, .NET {Environment.Version})");
Console.WriteLine($"# cpu={Environment.ProcessorCount} cores, gc={(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}");

var results = new List<string>();
RunFileBackend(results);

string? cs = Environment.GetEnvironmentVariable("OPTIROUTER_MARIADB_TEST");
if (!string.IsNullOrWhiteSpace(cs))
{
    RunMariaDbBackend(results, cs);
}
else
{
    results.Add("| (设置 OPTIROUTER_MARIADB_TEST 以运行 MariaDB 后端测量) | | | |");
}

Console.WriteLine();
Console.WriteLine("| 后端 | key 数 | 并发 | p50 µs | p95 µs | p99 µs | B/op(单线程) | 吞吐 ops/s |");
Console.WriteLine("|---|---|---|---|---|---|---|---|");
foreach (var line in results)
    Console.WriteLine(line);
return;

// ---------------------------------------------------------------- file backend

void RunFileBackend(List<string> results)
{
    foreach (int n in new[] { 1, 10, 100, 1000, 5000 })
    {
        string dir = Path.Combine(Path.GetTempPath(), "optirouter-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "client-keys.json");
        SeedKeyFile(file, n);

        using var service = new ClientKeyService(file, NullLogger<ClientKeyService>.Instance, flushInterval: TimeSpan.Zero);
        Measure(results, $"file valid ({n} keys)", service, keyCount: n, Plaintext);
        if (n >= 1000)
            Measure(results, $"file INVALID ({n} keys)", service, keyCount: n, "opti-key-nonexistent");
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }
}

void SeedKeyFile(string file, int n)
{
    // 直写 JSON（CreateKey 逐个建是 O(N²) 全文件重写，播种不必付这个代价）。
    // 基准明文固定；其余 key 是随机哈希占位（不会被基准明文命中）。
    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Plaintext)));
    var keys = new List<ClientKeyInfo>(n);
    for (int i = 0; i < n; i++)
    {
        keys.Add(new ClientKeyInfo
        {
            KeyId = "bench-" + i.ToString("D6"),
            KeyHash = i == 0 ? hash : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("bench-" + Guid.NewGuid().ToString("N")))),
            KeyPrefix = "opti-key-",
            TenantName = "bench-" + i,
            DailyBudgetUsd = 0m,
            MaxQps = 1_000_000,
            Enabled = true
        });
    }

    var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    File.WriteAllText(file, JsonSerializer.Serialize(keys, options));
}

// ---------------------------------------------------------------- mariadb backend

void RunMariaDbBackend(List<string> results, string cs)
{
    string db = "optirouter_bench_" + Guid.NewGuid().ToString("N")[..8];
    string firstPlaintext = "";
    string scratchCs = cs.Contains("Database=")
        ? System.Text.RegularExpressions.Regex.Replace(cs, @"Database=[^;]*", $"Database={db}")
        : cs.TrimEnd(';') + $";Database={db}";
    string serverCs = System.Text.RegularExpressions.Regex.Replace(cs, @"Database=[^;]*;?", "");

    using (var conn = new MySqlConnector.MySqlConnection(serverCs))
    {
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE IF NOT EXISTS `{db}` CHARACTER SET utf8mb4";
        cmd.ExecuteNonQuery();
    }

    try
    {
        foreach (int n in new[] { 1, 100, 1000 })
        {
            using var service = new ClientKeyService(
                filePath: "n/a.json",
                logger: NullLogger<ClientKeyService>.Instance,
                flushInterval: TimeSpan.Zero,
                mariaDbConnectionString: scratchCs);

            for (int i = 0; i < n; i++)
            {
                var (plaintext, _) = service.CreateKey($"bench-{n}-{i}", dailyBudgetUsd: 0m, maxQps: 1_000_000);
                if (i == 0) firstPlaintext = plaintext;
            }

            // 有效明文 → 走 DB 准入（TryAdmit 真实往返，全局锁内串行）。
            Measure(results, $"mariadb valid ({n} keys)", service, keyCount: n, firstPlaintext);
            if (n >= 1000)
                Measure(results, $"mariadb INVALID ({n} keys)", service, keyCount: n, "opti-key-nonexistent");
        }
    }
    finally
    {
        using var conn = new MySqlConnector.MySqlConnection(serverCs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS `{db}`";
        cmd.ExecuteNonQuery();
    }
}

// ---------------------------------------------------------------- measurement

/// <summary>单线程延迟/分配 + 多线程吞吐，一组配置一行结果。plaintext 决定走命中/Invalid 路径。</summary>
void Measure(List<string> results, string label, ClientKeyService service, int keyCount, string plaintext)
{
    // 预热（JIT + 缓存装载）。
    for (int i = 0; i < WarmupOps; i++)
        service.AuthorizeRequest(plaintext);

    // 单线程延迟 + 分配量。
    var latencies = new long[LatencyOps];
    long allocStart = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < LatencyOps; i++)
    {
        long start = Stopwatch.GetTimestamp();
        service.AuthorizeRequest(plaintext);
        latencies[i] = Stopwatch.GetTimestamp() - start;
    }
    long allocPerOp = (GC.GetAllocatedBytesForCurrentThread() - allocStart) / LatencyOps;
    Array.Sort(latencies);
    double p50 = Us(latencies, 50), p95 = Us(latencies, 95), p99 = Us(latencies, 99);

    // 多线程吞吐（并发 = 锁竞争压力）。
    double throughput = Throughput(service, plaintext, concurrency: 1);
    double throughputC32 = Throughput(service, plaintext, concurrency: 32);

    results.Add($"| {label} | {keyCount} | 1 | {p50:F1} | {p95:F1} | {p99:F1} | {allocPerOp} | {throughput:F0} |");
    results.Add($"| {label} | {keyCount} | 32 | — | — | — | — | {throughputC32:F0} |");
}

double Throughput(ClientKeyService service, string plaintext, int concurrency)
{
    long ops = 0;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ThroughputSeconds));
    var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
    {
        while (!cts.IsCancellationRequested)
        {
            service.AuthorizeRequest(plaintext);
            Interlocked.Increment(ref ops);
        }
    })).ToArray();
    Task.WhenAll(tasks).GetAwaiter().GetResult();
    return ops / (double)ThroughputSeconds;
}

static double Us(long[] sortedTicks, int percentile)
{
    int index = Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sortedTicks.Length) - 1, 0, sortedTicks.Length - 1);
    return sortedTicks[index] * 1_000_000.0 / Stopwatch.Frequency;
}
