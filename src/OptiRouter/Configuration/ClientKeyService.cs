using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace OptiRouter.Configuration;

/// <summary>
/// 客户端 API Key 与多租户配额管理服务。持久化至 client-keys.json。
/// 密钥以 SHA256 哈希存储（KeyHash），明文仅在创建时返回一次；KeyId 为公开标识用于管理引用。
/// </summary>
/// <remarks>
/// 花费持久化为去抖批量：<see cref="RecordSpend"/> 仅同步更新内存中的 <see cref="ClientKeyInfo.DailySpendUsd"/>
/// （保证 <see cref="AuthorizeRequest"/> 的预算/QPS 管控即时生效），不再每请求同步 fsync 整个文件；
/// 后台定时器按 <see cref="_flushInterval"/> 合并落盘。高 QPS 下数千笔花费合并为一次磁盘写。
/// 进程崩溃最多丢失一个 flush 窗口内的花费记录（成本统计而非资金，可接受）。
/// 需要即时落盘时调 <see cref="Flush"/>；DI 注册的单例实现 <see cref="IDisposable"/>，
/// 容器关闭时自动 Dispose 触发最终 Flush。
/// </remarks>
public sealed class ClientKeyService : IDisposable
{
    /// <summary>默认花费落盘去抖间隔（高 QPS 合并写）。</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(3);

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, QpsWindow> _qpsWindows = new(StringComparer.Ordinal);
    private List<ClientKeyInfo>? _cachedKeys;
    private DateTime _lastFileWriteTimeUtc = DateTime.MinValue;
    private readonly TimeSpan _flushInterval;
    private readonly ITimer? _flushTimer;
    private bool _spendDirty;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ClientKeyService(
        string? filePath,
        ILogger<ClientKeyService> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? flushInterval = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine("data", "client-keys.json")
            : filePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _flushInterval = flushInterval ?? DefaultFlushInterval;

        EnsureFileExists();

        // 去抖落盘定时器：周期检查 _spendDirty，合并写一次。Zero/负值禁用定时器（仅供测试）。
        if (_flushInterval > TimeSpan.Zero)
        {
            _flushTimer = _timeProvider.CreateTimer(
                _ => FlushIfDirty(),
                null,
                _flushInterval,
                _flushInterval);
        }
    }

    private void EnsureFileExists()
    {
        lock (_gate)
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(_filePath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_filePath))
            {
                // New installations deliberately start empty. A plaintext default key must never
                // be generated, logged, or written to disk.
                SaveKeysToFile(new List<ClientKeyInfo>());
                return;
            }

            // Validate an existing file, but never replace it when it is corrupt or in a legacy
            // plaintext format. Callers must see the failure so an operator can recover the file.
            _ = GetCachedOrLoadKeysNoLock();
        }
    }

    private List<ClientKeyInfo> GetCachedOrLoadKeysNoLock()
    {
        if (File.Exists(_filePath))
        {
            var lastWrite = File.GetLastWriteTimeUtc(_filePath);
            if (_cachedKeys is not null && lastWrite == _lastFileWriteTimeUtc)
            {
                return _cachedKeys;
            }
        }

        var loaded = LoadKeysFromFile();
        _cachedKeys = loaded;
        _lastFileWriteTimeUtc = File.Exists(_filePath) ? File.GetLastWriteTimeUtc(_filePath) : DateTime.MinValue;
        return loaded;
    }

    private List<ClientKeyInfo> LoadKeysFromFile()
    {
        if (!File.Exists(_filePath))
            return new List<ClientKeyInfo>();

        string json = File.ReadAllText(_filePath);
        var keys = JsonSerializer.Deserialize<List<ClientKeyInfo>>(json, JsonOpts)
            ?? throw new InvalidDataException("client-keys.json must contain a JSON array.");

        foreach (var key in keys)
            ValidatePersistedKey(key);

        return keys;
    }

    private static void ValidatePersistedKey(ClientKeyInfo key)
    {
        if (key is null
            || string.IsNullOrWhiteSpace(key.KeyId)
            || string.IsNullOrWhiteSpace(key.KeyHash)
            || string.IsNullOrWhiteSpace(key.KeyPrefix)
            || string.IsNullOrWhiteSpace(key.TenantName))
        {
            throw new InvalidDataException("client-keys.json contains an incomplete client key.");
        }

        // Validate the persisted representation without accepting a legacy plaintext value in the
        // hash field. Existing uppercase and lowercase SHA-256 hex strings remain compatible.
        if (!TryDecodeHash(key.KeyHash, out _))
        {
            throw new InvalidDataException("client-keys.json contains a non-SHA256 client key hash.");
        }
    }

    /// <summary>
    /// Returns a snapshot copy of all persisted key metadata. KeyHash is included for internal callers only.
    /// 返回拷贝而非缓存本体，避免外部调用方修改列表污染内部缓存（内部写路径仍直接操作缓存本体）。
    /// </summary>
    public List<ClientKeyInfo> GetAllKeys()
    {
        lock (_gate)
            return new List<ClientKeyInfo>(GetCachedOrLoadKeysNoLock());
    }

    /// <summary>
    /// 认证一次客户端请求，并在同一个锁内消耗其固定一秒 QPS 配额。
    /// 全局代理密钥不经过此服务；不存在、禁用或配额耗尽的租户都会返回可判别结果。
    /// </summary>
    public ClientKeyAuthorizationResult AuthorizeRequest(string? plaintext)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(plaintext))
                return ClientKeyAuthorizationResult.Invalid;

            byte[] candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
            ClientKeyInfo? matched = null;
            var keys = GetCachedOrLoadKeysNoLock();

            foreach (var key in keys)
            {
                // Compare every candidate even after a match so lookup duration does not reveal
                // the matching key's position. Invalid persisted hashes are rejected at load time.
                bool valid = TryDecodeHash(key.KeyHash, out byte[] storedHash);
                storedHash = valid ? storedHash : new byte[32];
                bool equal = CryptographicOperations.FixedTimeEquals(candidateHash, storedHash);
                if (valid && equal && matched is null)
                    matched = key;
            }

            if (matched is null)
                return ClientKeyAuthorizationResult.Invalid;

            ClientKeyAuthorizationResult identity(ClientKeyAuthorizationStatus status, int retryAfterSeconds = 0)
                => new(status, matched.KeyId, matched.TenantName, matched.KeyPrefix, retryAfterSeconds);

            if (!matched.Enabled)
                return identity(ClientKeyAuthorizationStatus.Disabled);

            DateTime today = UtcToday();
            bool changed = RollDailySpend(matched, today);
            long currentWindow = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (!_qpsWindows.TryGetValue(matched.KeyId, out var window)
                || window.StartUnixSecond != currentWindow)
            {
                window = new QpsWindow(currentWindow, 0);
            }

            int maxQps = Math.Max(1, matched.MaxQps);
            if (window.Count >= maxQps)
            {
                if (changed)
                    SaveKeysToFile(keys);

                return identity(ClientKeyAuthorizationStatus.RateLimited, RetryAfterSecondsForWindow(currentWindow));
            }

            if (matched.DailyBudgetUsd > 0m && matched.DailySpendUsd >= matched.DailyBudgetUsd)
            {
                if (changed)
                    SaveKeysToFile(keys);

                return identity(ClientKeyAuthorizationStatus.BudgetExhausted, RetryAfterSecondsForDay(today));
            }

            _qpsWindows[matched.KeyId] = window with { Count = window.Count + 1 };
            matched.DailyRequestCount++;
            if (changed)
                SaveKeysToFile(keys);
            else
                _spendDirty = true; // 请求计数变化由去抖定时器合并落盘

            return identity(ClientKeyAuthorizationStatus.Authorized);
        }
    }

    /// <summary>
    /// Adds actual request cost to a tenant's UTC daily spend.内存值即时更新（预算/QPS 管控即时生效），
    /// 文件持久化由后台去抖定时器合并执行（见类备注），不再每请求同步 fsync。需要即时落盘请调 <see cref="Flush"/>。
    /// Unknown keys and non-positive costs are ignored so accounting cannot create credit.
    /// </summary>
    public void RecordSpend(string keyId, decimal cost)
    {
        if (string.IsNullOrWhiteSpace(keyId) || cost <= 0m)
            return;

        lock (_gate)
        {
            var keys = GetCachedOrLoadKeysNoLock();
            var item = keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
            if (item is null)
                return;

            DateTime today = UtcToday();
            RollDailySpend(item, today);
            item.DailySpendUsd += cost;
            item.DailySpendDateUtc ??= today;
            _spendDirty = true;
        }
    }

    /// <summary>
    /// 立即把挂起的花费同步落盘。供测试、手动持久化与优雅关闭使用。
    /// 多次调用幂等；无脏数据时为 no-op。
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed || !_spendDirty) return;
            SaveKeysToFile(GetCachedOrLoadKeysNoLock());
            _spendDirty = false;
        }
    }

    /// <summary>定时器回调：脏则合并落盘。异常吞掉以免后台任务死亡（下一周期重试）。</summary>
    private void FlushIfDirty()
    {
        try { Flush(); }
        catch
        {
            // 后台落盘失败不影响请求路径；下一周期会再次尝试。
        }
    }

    /// <summary>
    /// 释放定时器并尽力把挂起的花费最终落盘。DI 容器关闭单例时自动触发（优雅关闭即不丢账）。
    /// </summary>
    public void Dispose()
    {
        _flushTimer?.Dispose();

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            if (_spendDirty)
            {
                try
                {
                    SaveKeysToFile(GetCachedOrLoadKeysNoLock());
                    _spendDirty = false;
                }
                catch
                {
                    // 优雅关闭期间的最终落盘为 best-effort，失败不抛以免中断关闭。
                }
            }
        }
    }

    /// <summary>
    /// 创建新密钥。返回一次性明文（调用方须立即交付租户，不再持久化也不再可重取）与持久化的 KeyInfo。
    /// </summary>
    public (string PlaintextKey, ClientKeyInfo Info) CreateKey(
        string tenantName,
        decimal dailyBudgetUsd = 100.0m,
        int maxQps = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantName);
        lock (_gate)
        {
            var keys = GetCachedOrLoadKeysNoLock();
            var (plaintext, info) = Build(tenantName.Trim(), dailyBudgetUsd, maxQps);
            keys.Add(info);
            SaveKeysToFile(keys);
            return (plaintext, info);
        }
    }

    public bool UpdateKey(string keyId, bool? enabled, decimal? dailyBudgetUsd, int? maxQps)
    {
        lock (_gate)
        {
            var keys = GetCachedOrLoadKeysNoLock();
            var item = keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
            if (item is null) return false;

            if (enabled.HasValue) item.Enabled = enabled.Value;
            if (dailyBudgetUsd.HasValue && dailyBudgetUsd.Value >= 0) item.DailyBudgetUsd = dailyBudgetUsd.Value;
            if (maxQps.HasValue && maxQps.Value > 0) item.MaxQps = maxQps.Value;

            SaveKeysToFile(keys);
            return true;
        }
    }

    public bool DeleteKey(string keyId)
    {
        lock (_gate)
        {
            var keys = GetCachedOrLoadKeysNoLock();
            int removed = keys.RemoveAll(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
            if (removed > 0)
            {
                SaveKeysToFile(keys);
                _qpsWindows.Remove(keyId);
                return true;
            }

            return false;
        }
    }

    private (string Plaintext, ClientKeyInfo Info) Build(string tenantName, decimal dailyBudgetUsd, int maxQps)
    {
        string plaintext = "opti-key-" + Guid.NewGuid().ToString("N");
        var info = new ClientKeyInfo
        {
            KeyId = "kid-" + Guid.NewGuid().ToString("N")[..12],
            KeyHash = HashKey(plaintext),
            KeyPrefix = plaintext.Length <= 12 ? plaintext : plaintext[..12] + "…",
            TenantName = tenantName,
            DailyBudgetUsd = Math.Max(0, dailyBudgetUsd),
            MaxQps = Math.Max(1, maxQps),
            Enabled = true,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            DailySpendDateUtc = UtcToday()
        };
        return (plaintext, info);
    }

    private static string HashKey(string plaintext)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(hash);
    }

    private static bool TryDecodeHash(string value, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (value.Length != 64)
            return false;

        try
        {
            decoded = Convert.FromHexString(value);
            return decoded.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool RollDailySpend(ClientKeyInfo item, DateTime today)
    {
        if (item.DailySpendDateUtc is null)
        {
            // Legacy hashed files had no date. Treat their existing spend as today's spend and
            // persist the date on the next mutation, preserving backward compatibility.
            item.DailySpendDateUtc = today;
            return true;
        }

        if (item.DailySpendDateUtc.Value.Date == today)
            return false;

        item.DailySpendUsd = 0m;
        item.DailyRequestCount = 0;
        item.DailySpendDateUtc = today;
        return true;
    }

    private DateTime UtcToday() => _timeProvider.GetUtcNow().UtcDateTime.Date;

    private int RetryAfterSecondsForWindow(long currentWindow)
    {
        // The fixed window ends at the next whole Unix second. Returning at least one avoids a
        // client retrying in the same exhausted window due to sub-second rounding.
        long seconds = currentWindow + 1 - _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        return Math.Max(1, seconds > int.MaxValue ? int.MaxValue : (int)seconds);
    }

    private int RetryAfterSecondsForDay(DateTime today)
    {
        DateTimeOffset next = new(today.AddDays(1), TimeSpan.Zero);
        double seconds = (next - _timeProvider.GetUtcNow()).TotalSeconds;
        return Math.Max(1, seconds >= int.MaxValue ? int.MaxValue : (int)Math.Ceiling(seconds));
    }

    private void SaveKeysToFile(List<ClientKeyInfo> keys)
    {
        string json = JsonSerializer.Serialize(keys, JsonOpts);
        string fullPath = Path.GetFullPath(_filePath);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("client-keys.json has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.SequentialScan))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, fullPath);

            // 收紧文件权限：仅所有者可读写（0600），防止同机其他用户读取 KeyHash/KeyPrefix。
            SetOwnerOnlyPermissions(fullPath);

            _cachedKeys = keys;
            _lastFileWriteTimeUtc = File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : DateTime.MinValue;
            // 整文件已写入：任何挂起的花费随之落盘，清去抖脏标志（覆盖所有 SaveKeysToFile 调用点）。
            _spendDirty = false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original persistence exception. A leftover uniquely named temp
                // file is recoverable and cannot replace the valid target on its own.
            }
        }
    }

    private readonly record struct QpsWindow(long StartUnixSecond, int Count);

    /// <summary>
    /// 设置文件权限为仅所有者可读写（Linux: chmod 600，Windows: 仅限当前用户 ACL）。
    /// 静默失败：文件已写入，权限设置失败不回滚写入（best-effort 纵深防御）。
    /// </summary>
    private static void SetOwnerOnlyPermissions(string path)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // chmod 0600：仅所有者读写，组与其他无权限
                System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            // Windows：File.Replace/Move 继承父目录 ACL，通常已是当前用户私有。
            // 如需更严格限制，可在此处设置显式 ACL（当前实现跳过，依赖 NTFS 默认继承）。
        }
        catch
        {
            // best-effort：权限设置失败不阻断正常写入流程。
        }
    }
}

public enum ClientKeyAuthorizationStatus
{
    Authorized,
    Invalid,
    Disabled,
    RateLimited,
    BudgetExhausted
}

/// <summary>Immutable outcome of a tenant-key authorization attempt.</summary>
public sealed record ClientKeyAuthorizationResult(
    ClientKeyAuthorizationStatus Status,
    string? KeyId = null,
    string? TenantName = null,
    string? KeyPrefix = null,
    int RetryAfterSeconds = 0)
{
    public bool IsAuthorized => Status == ClientKeyAuthorizationStatus.Authorized;

    public static ClientKeyAuthorizationResult Invalid { get; } =
        new(ClientKeyAuthorizationStatus.Invalid);
}

public sealed class ClientKeyInfo
{
    /// <summary>公开标识（kid- 前缀），用于管理 API 路径与 UI 引用。与明文解耦。</summary>
    public required string KeyId { get; set; }

    /// <summary>SHA256(plaintext) 十六进制，持久化用于鉴权比对。绝不返回前端。</summary>
    public required string KeyHash { get; set; }

    /// <summary>明文前 12 字符指纹，供运维识别。无法还原明文。</summary>
    public required string KeyPrefix { get; set; }

    public required string TenantName { get; set; }
    public decimal DailyBudgetUsd { get; set; } = 100.0m;
    public decimal DailySpendUsd { get; set; } = 0.0m;

    /// <summary>当日成功授权的请求数（UTC 日滚动，与 DailySpendUsd 同窗口）。</summary>
    public int DailyRequestCount { get; set; }
    public int MaxQps { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC date associated with DailySpendUsd. Nullable for compatibility with existing hashed
    /// files written before daily rollover tracking was introduced.
    /// </summary>
    public DateTime? DailySpendDateUtc { get; set; }
}
