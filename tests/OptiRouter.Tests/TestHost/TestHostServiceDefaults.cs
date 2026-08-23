using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

namespace OptiRouter.Tests;

/// <summary>
/// 集成测试宿主的公共默认值。测试命名空间下的工厂直接调用（父命名空间隐式可见，无需 using）。
/// </summary>
public static class TestHostServiceDefaults
{
    /// <summary>
    /// 移除全部后台 HostedService。健康探针会在测试中途给模型打熔断标记、会话亲和预热
    /// 会改写粘性缓存、指标 gauge 定时刷新会与断言竞争——这些后台任务改写的是请求间共享
    /// 状态，是 flaky 的根源；测试需要相关状态时在测试内自行构造。
    /// 在 WebApplicationFactory 的 ConfigureServices（Program.cs 注册之后执行）中调用。
    /// </summary>
    public static void RemoveBackgroundServices(this IServiceCollection services)
    {
        services.RemoveAll(typeof(IHostedService));
    }

    /// <summary>
    /// 预置已知明文的高配额租户 key（替换 Program.cs 注册的 ClientKeyService）。
    /// 全局 ProxyApiKey 已移除：/v1 请求一律走租户准入，测试客户端照旧发送 Bearer&lt;plaintext&gt;。
    /// 同时把 AdminKeyStore 换绑到本工厂独立的临时 security 库：管理密钥哈希落配置库后，
    /// 共享默认配置库会让首个工厂的种子压倒其他工厂的 AdminApiKey 配置（跨测试耦合）。
    /// </summary>
    public static void UseFixedTenantKey(this IServiceCollection services, string plaintext)
    {
        services.RemoveAll<ClientKeyService>();
        services.AddSingleton(_ => BuildSeededClientKeyService(plaintext));

        services.RemoveAll<OptiRouter.Configuration.AdminKeyStore>();
        string securityDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "optirouter-test-security-" + Guid.NewGuid().ToString("N") + ".db");
        services.AddSingleton(sp => new OptiRouter.Configuration.AdminKeyStore(
            new AppConfigDbStore(securityDb),
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<OptiRouter.Configuration.AdminKeyStore>>()));
    }

    private static ClientKeyService BuildSeededClientKeyService(string plaintext)
    {
        // ClientKeyService 只能 CreateKey 生成随机明文；已知明文走文件预置（SHA256 哈希直写）。
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optirouter-test-keys-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string file = System.IO.Path.Combine(dir, "client-keys.json");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        // 空明文 = 不种子任何 key（安全测试的"未配置"场景：一切凭证都应 401）。
        if (string.IsNullOrEmpty(plaintext))
        {
            File.WriteAllText(file, JsonSerializer.Serialize(new List<ClientKeyInfo>(), options));
            return new ClientKeyService(file, NullLogger<ClientKeyService>.Instance, flushInterval: TimeSpan.Zero);
        }

        var info = new ClientKeyInfo
        {
            KeyId = "test-key",
            KeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))),
            KeyPrefix = plaintext.Length > 8 ? plaintext[..8] : plaintext,
            TenantName = "test-tenant",
            MaxQps = 1_000_000,
            DailyBudgetUsd = 0m, // 不限预算：预算场景测试自行构造
            Enabled = true
        };
        File.WriteAllText(file, JsonSerializer.Serialize(new List<ClientKeyInfo> { info }, options));
        return new ClientKeyService(file, NullLogger<ClientKeyService>.Instance, flushInterval: TimeSpan.Zero);
    }
}
