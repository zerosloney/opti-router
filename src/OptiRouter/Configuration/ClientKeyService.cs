using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using MySqlConnector;

namespace OptiRouter.Configuration;

/// <summary>
/// 客户端 API Key 与多租户配额管理服务。默认持久化至 client-keys.json；
/// 构造时传入 MariaDB 连接串（<c>OptiRouter:ConfigDbConnectionString</c>）
/// 则切换为 MariaDB 后端（表 optirouter_client_keys，见 <see cref="MariaDbClientKeyStore"/>）。
/// 密钥以 SHA256 哈希存储（KeyHash），明文仅在创建时返回一次；KeyId 为公开标识用于管理引用。
/// </summary>
/// <remarks>
/// 花费持久化为去抖批量：<see cref="RecordSpend"/> 仅同步更新内存中的 <see cref="ClientKeyInfo.DailySpendUsd"/>
/// （保证 <see cref="AuthorizeRequest"/> 的预算/QPS 管控即时生效），不再每请求同步落盘/落库；
/// 后台定时器按 <see cref="_flushInterval"/> 合并持久化。高 QPS 下数千笔花费合并为一次写。
/// 进程崩溃最多丢失一个 flush 窗口内的花费记录（成本统计而非资金，可接受）。
/// 需要即时持久化时调 <see cref="Flush"/>；DI 注册的单例实现 <see cref="IDisposable"/>，
/// 容器关闭时自动 Dispose 触发最终 Flush。
/// <para>
/// DB 后端多实例语义：建/改/删为按行写，各实例互不覆盖；花费以相对增量提交并在库内累加；
/// QPS 固定秒窗与日预算经库端行锁单条 UPDATE 原子判定（<b>全局口径</b>，预算/上限引用列值，
/// 即时反映其他实例的管理端修改）；缓存按 30 秒周期重载（重载前先提交本实例增量），
/// 实例间对 key 列表与全局花费最终一致。预算判定基于库内已提交花费，各实例 ≤3 秒
/// flush 窗口内的未提交花费可能造成轻微超订（后付费记账固有）。MariaDB 不可达时
/// 准入降级为进程内口径并按状态迁移记一次日志，恢复后自动回切。
/// </para>
/// </remarks>
public sealed class ClientKeyService : IDisposable
{
    /// <summary>默认花费落盘去抖间隔（高 QPS 合并写）。</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(3);

    // DB 模式缓存刷新间隔：超过后下次读取先重载（多实例可见其他实例的建/改/删 key，最终一致）。
    private static readonly TimeSpan DbCacheRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly string _filePath;
    // MariaDB 后端；null = JSON 文件后端（null! 以免文件代码路径逐处判空，方法入口先委托 DB 后端）。
    private readonly MariaDbClientKeyStore? _mariaDb = null!;
    private readonly ILogger<ClientKeyService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, QpsWindow> _qpsWindows = new(StringComparer.Ordinal);
    // DB 模式待提交的按 key 花费增量（本实例视角），flush 时以相对增量提交到库；
    // 请求数不走增量——由全局准入语句在库端原子计数。
    private readonly Dictionary<string, decimal> _pendingDeltas = new(StringComparer.Ordinal);
    // in-flight 花费预留（按 keyId，请求发起前预扣、结束释放）：租户预算与全局账本同源的
    // TOCTOU 防护——预算检查在授权时、计费在请求完成后，窗口内并发请求会集体越过预算线。
    // 仅进程内口径：不落库、不进增量，多节点各自保守（与 CostLedger 预留语义一致）。
    private readonly Dictionary<string, decimal> _spendReservations = new(StringComparer.Ordinal);
    // 0 = DB 准入正常，1 = 已降级进程内口径。按状态迁移记日志，故障期间不逐请求刷屏。
    private int _dbAuthDegraded;
    private List<ClientKeyInfo>? _cachedKeys;
    private DateTime _lastFileWriteTimeUtc = DateTime.MinValue;
    private DateTime _lastDbLoadUtc = DateTime.MinValue;
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
        TimeSpan? flushInterval = null,
        string? mariaDbConnectionString = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(mariaDbConnectionString))
        {
            _mariaDb = new MariaDbClientKeyStore(mariaDbConnectionString);
            _filePath = string.Empty;
        }
        else
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? Path.Combine("data", "client-keys.json")
                : filePath;
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _flushInterval = flushInterval ?? DefaultFlushInterval;

        EnsureStorageReady();

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

    /// <summary>启动校验：文件后端确保文件存在且结构合法；DB 后端建表并加载校验缓存。</summary>
    private void EnsureStorageReady()
    {
        lock (_gate)
        {
            if (_mariaDb is not null)
            {
                var loaded = _mariaDb.Load();
                foreach (var key in loaded)
                    ValidatePersistedKey(key);
                _cachedKeys = loaded;
                _lastDbLoadUtc = _timeProvider.GetUtcNow().UtcDateTime;
                return;
            }

            string? dir = Path.GetDirectoryName(Path.GetFullPath(_filePath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (!File.Exists(_filePath))
            {
                // New installations deliberately start empty. A plaintext default key must never
                // be generated, logged, or written to disk.
                PersistKeys(new List<ClientKeyInfo>());
                return;
            }

            // Validate an existing file, but never replace it when it is corrupt or in a legacy
            // plaintext format. Callers must see the failure so an operator can recover the file.
            _ = GetCachedOrLoadKeysNoLock();
        }
    }

    private List<ClientKeyInfo> GetCachedOrLoadKeysNoLock()
    {
        // DB 后端：本进程为写者之一（多实例并发），缓存按 DbCacheRefreshInterval 周期性重载，
        // 重载前先提交本实例挂起的增量，保证全局值不丢。
        if (_mariaDb is not null)
        {
            if (_cachedKeys is not null
                && _timeProvider.GetUtcNow().UtcDateTime - _lastDbLoadUtc < DbCacheRefreshInterval)
            {
                return _cachedKeys;
            }

            FlushPendingDeltasNoLock();
            var dbLoaded = _mariaDb.Load();
            foreach (var key in dbLoaded)
                ValidatePersistedKey(key);
            _cachedKeys = dbLoaded;
            _lastDbLoadUtc = _timeProvider.GetUtcNow().UtcDateTime;
            return _cachedKeys;
        }

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

            if (_mariaDb is not null)
            {
                // 记忆体预算快速拒绝（含本实例未提交增量 + in-flight 预留），预算耗尽时省一次 DB 往返。
                if (IsBudgetExhaustedEffective(matched))
                    return identity(ClientKeyAuthorizationStatus.BudgetExhausted, RetryAfterSecondsForDay(today));

                var dbResult = AuthorizeViaDbNoLock(matched, identity, today, currentWindow);
                // DB 放行后的进程内复核：TryAdmit 只看库内已提交花费，本实例 in-flight 预留
                // 对库不可见——预留口径已超限时本地保守拒绝，堵住库口径的同一 TOCTOU 窗口。
                if (dbResult.Status == ClientKeyAuthorizationStatus.Authorized && IsBudgetExhaustedEffective(matched))
                    return identity(ClientKeyAuthorizationStatus.BudgetExhausted, RetryAfterSecondsForDay(today));
                return dbResult;
            }

            // 文件后端：进程内固定秒窗 QPS + 记忆体预算。
            if (!_qpsWindows.TryGetValue(matched.KeyId, out var window)
                || window.StartUnixSecond != currentWindow)
            {
                window = new QpsWindow(currentWindow, 0);
            }

            int maxQps = Math.Max(1, matched.MaxQps);
            if (window.Count >= maxQps)
            {
                if (changed)
                    PersistKeys(keys);

                return identity(ClientKeyAuthorizationStatus.RateLimited, RetryAfterSecondsForWindow(currentWindow));
            }

            if (IsBudgetExhaustedEffective(matched))
            {
                if (changed)
                    PersistKeys(keys);

                return identity(ClientKeyAuthorizationStatus.BudgetExhausted, RetryAfterSecondsForDay(today));
            }

            _qpsWindows[matched.KeyId] = window with { Count = window.Count + 1 };
            matched.DailyRequestCount++;
            if (changed)
                PersistKeys(keys);
            else
                _spendDirty = true; // 请求计数变化由去抖定时器合并落盘

            return identity(ClientKeyAuthorizationStatus.Authorized);
        }
    }

    /// <summary>
    /// DB 后端全局准入：QPS 固定秒窗与日预算在 MariaDB 行锁下原子判定（多实例共享口径）。
    /// MariaDB 不可达时降级为进程内口径（与文件模式同语义）并按状态迁移记一次日志，恢复后自动回切。
    /// </summary>
    private ClientKeyAuthorizationResult AuthorizeViaDbNoLock(
        ClientKeyInfo matched,
        Func<ClientKeyAuthorizationStatus, int, ClientKeyAuthorizationResult> identity,
        DateTime today,
        long currentWindow)
    {
        try
        {
            var admission = _mariaDb!.TryAdmit(matched.KeyId, today, currentWindow);
            MarkAuthRecovered();
            switch (admission)
            {
                case ClientKeyAdmission.Admitted:
                    matched.DailyRequestCount++; // 本地近似值（UI/预算快速路径用），权威计数在库内
                    return identity(ClientKeyAuthorizationStatus.Authorized, 0);

                case ClientKeyAdmission.BudgetExhausted:
                    return identity(ClientKeyAuthorizationStatus.BudgetExhausted, RetryAfterSecondsForDay(today));

                case ClientKeyAdmission.RateLimited:
                    return identity(ClientKeyAuthorizationStatus.RateLimited, RetryAfterSecondsForWindow(currentWindow));

                case ClientKeyAdmission.Disabled:
                    return identity(ClientKeyAuthorizationStatus.Disabled, 0);

                default:
                    return ClientKeyAuthorizationResult.Invalid; // 行已被其他实例删除，缓存刷新后即正确
            }
        }
        catch (Exception ex) when (ex is MySqlException or IOException)
        {
            MarkAuthDegraded(ex);
            // 降级：进程内固定秒窗 + 记忆体预算（含未提交增量）。
            if (!_qpsWindows.TryGetValue(matched.KeyId, out var window)
                || window.StartUnixSecond != currentWindow)
            {
                window = new QpsWindow(currentWindow, 0);
            }

            if (window.Count >= Math.Max(1, matched.MaxQps))
                return identity(ClientKeyAuthorizationStatus.RateLimited, RetryAfterSecondsForWindow(currentWindow));

            _qpsWindows[matched.KeyId] = window with { Count = window.Count + 1 };
            matched.DailyRequestCount++;
            return identity(ClientKeyAuthorizationStatus.Authorized, 0);
        }
    }

    private void MarkAuthDegraded(Exception ex)
    {
        if (Interlocked.Exchange(ref _dbAuthDegraded, 1) == 0)
        {
            _logger.LogError(ex,
                "Client key global admission degraded: MariaDB unreachable, falling back to per-process QPS/budget. " +
                "Limits are per-node until MariaDB recovers");
        }
    }

    private void MarkAuthRecovered()
    {
        if (Interlocked.Exchange(ref _dbAuthDegraded, 0) == 1)
            _logger.LogWarning("Client key global admission recovered; QPS/budget limits are global again");
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
            if (_mariaDb is not null)
                TrackSpendDelta(item.KeyId, cost);
            else
                _spendDirty = true;
        }
    }

    /// <summary>
    /// 预扣租户 in-flight 花费（预算 TOCTOU 防护，与 <see cref="RecordSpend"/> 对称）。
    /// 必须与 <see cref="ReleaseSpend"/> 严格配对（调用方 try/finally 或 using 保证）。
    /// </summary>
    public void ReserveSpend(string keyId, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(keyId) || amount <= 0m)
            return;

        lock (_gate)
        {
            _spendReservations[keyId] = _spendReservations.GetValueOrDefault(keyId) + amount;
        }
    }

    /// <summary>
    /// 释放预留。clamp 到 0 防配对失误（如重复释放）导致负数。
    /// </summary>
    public void ReleaseSpend(string keyId, decimal amount)
    {
        if (string.IsNullOrWhiteSpace(keyId) || amount <= 0m)
            return;

        lock (_gate)
        {
            decimal remaining = Math.Max(0m, _spendReservations.GetValueOrDefault(keyId) - amount);
            if (remaining == 0m) _spendReservations.Remove(keyId);
            else _spendReservations[keyId] = remaining;
        }
    }

    /// <summary>
    /// 含 in-flight 预留的预算耗尽判定（已入账记忆值 + 预留）。须在 _gate 锁内调用。
    /// 跨午夜仍未释放的预留会计入新一天（保守方向，预留本身是分钟级瞬态，可接受）。
    /// </summary>
    private bool IsBudgetExhaustedEffective(ClientKeyInfo key)
    {
        if (key.DailyBudgetUsd <= 0m)
            return false;
        return key.DailySpendUsd + _spendReservations.GetValueOrDefault(key.KeyId) >= key.DailyBudgetUsd;
    }

    /// <summary>
    /// 立即把挂起的花费同步落盘/落库。供测试、手动持久化与优雅关闭使用。
    /// 多次调用幂等；无脏数据时为 no-op。
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_mariaDb is not null)
            {
                FlushPendingDeltasNoLock();
                return;
            }

            if (!_spendDirty) return;
            PersistKeys(GetCachedOrLoadKeysNoLock());
            _spendDirty = false;
        }
    }

    /// <summary>DB 后端：把本实例挂起的花费增量逐 key 提交到库；单 key 失败保留其余重试。</summary>
    private void FlushPendingDeltasNoLock()
    {
        if (_pendingDeltas.Count == 0) return;

        DateTime today = UtcToday();
        foreach (var keyId in _pendingDeltas.Keys.ToList())
        {
            _mariaDb!.ApplySpendDelta(keyId, today, _pendingDeltas[keyId]);
            _pendingDeltas.Remove(keyId);
        }
    }

    private void TrackSpendDelta(string keyId, decimal spendDelta)
    {
        _pendingDeltas.TryGetValue(keyId, out decimal current);
        _pendingDeltas[keyId] = current + spendDelta;
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
    /// 释放定时器并尽力把挂起的花费最终落盘/落库。DI 容器关闭单例时自动触发（优雅关闭即不丢账）。
    /// </summary>
    public void Dispose()
    {
        _flushTimer?.Dispose();

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_mariaDb is not null)
                    FlushPendingDeltasNoLock();
                else if (_spendDirty)
                    PersistKeys(GetCachedOrLoadKeysNoLock());
            }
            catch
            {
                // 优雅关闭期间的最终落盘为 best-effort，失败不抛以免中断关闭。
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
            if (_mariaDb is not null)
                _mariaDb.InsertKey(info); // 按行插入，不影响其他实例的行
            else
                PersistKeys(keys);
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
            // 与 CreateKey/Build 同口径：越界值钳到下界，而非静默忽略（创建/更新行为一致）。
            if (dailyBudgetUsd.HasValue) item.DailyBudgetUsd = Math.Max(0, dailyBudgetUsd.Value);
            if (maxQps.HasValue) item.MaxQps = Math.Max(1, maxQps.Value);

            if (_mariaDb is not null)
                _mariaDb.UpdateKeySettings(item); // 只写设置列，不触碰计数控（多实例安全）
            else
                PersistKeys(keys);
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
                if (_mariaDb is not null)
                    _mariaDb.DeleteKey(keyId); // 按行删除，不影响其他实例的行
                else
                    PersistKeys(keys);
                _qpsWindows.Remove(keyId);
                _pendingDeltas.Remove(keyId);
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

    /// <summary>文件后端：持久化全部 Key（原子重写 + fsync 快照语义）。仅 JSON 文件模式调用。</summary>
    private void PersistKeys(List<ClientKeyInfo> keys)
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
