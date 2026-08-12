using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Configuration;

/// <summary>
/// 客户端 API Key 与多租户配额管理服务。持久化至 client-keys.json。
/// 密钥以 SHA256 哈希存储（KeyHash），明文仅在创建时返回一次；KeyId 为公开标识用于管理引用。
/// </summary>
public sealed class ClientKeyService
{
    private readonly string _filePath;
    private readonly ILogger<ClientKeyService> _logger;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ClientKeyService(string filePath, ILogger<ClientKeyService> logger)
    {
        _filePath = filePath ?? Path.Combine("data", "client-keys.json");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        EnsureFileExists();
    }

    private void EnsureFileExists()
    {
        lock (_gate)
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(_filePath))
            {
                SeedAndSave();
                return;
            }

            // 重建策略（用户确认）：文件存在但格式旧（含明文 Key 无 KeyId/KeyHash）或损坏，
            // 删除后重新种子。既有明文密钥失效，避免反序列化失败被写操作覆盖丢失。
            try
            {
                GetAllKeys();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "client-keys.json unreadable or legacy plaintext format at {Path}; recreating with defaults (existing keys invalidated)",
                    _filePath);
                try { File.Delete(_filePath); } catch { /* best effort */ }
                SeedAndSave();
            }
        }
    }

    private void SeedAndSave()
    {
        // 默认种子的明文仅写日志一次（开发/运维首启获取），生产应通过 Dashboard 重新签发。
        var seeded = SeedDefaults();
        SaveKeysToFile(seeded.Select(t => t.Info).ToList());
        foreach (var (plaintext, info) in seeded)
        {
            _logger.LogWarning(
                "Seeded default client key for '{Tenant}': {Plaintext} (record now; only its hash is persisted)",
                info.TenantName, plaintext);
        }
    }

    private static List<(string Plaintext, ClientKeyInfo Info)> SeedDefaults()
    {
        return new List<(string, ClientKeyInfo)>
        {
            Build("默认核心管理租户", 1000.0m, 100),
            Build("财务部 App 端点", 50.0m, 20)
        };
    }

    /// <summary>
    /// 生成新明文 + KeyId/KeyHash/KeyPrefix（明文不进 KeyInfo，仅返回给调用方）。
    /// </summary>
    private static (string Plaintext, ClientKeyInfo Info) Build(string tenantName, decimal dailyBudgetUsd, int maxQps)
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
            CreatedAt = DateTime.UtcNow
        };
        return (plaintext, info);
    }

    private static string HashKey(string plaintext)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(hash);
    }

    public List<ClientKeyInfo> GetAllKeys()
    {
        lock (_gate)
        {
            // 不吞解析异常：EnsureFileExists 已处理首启重建；运行时损坏应让调用方感知，
            // 避免 CreateKey/UpdateKey/DeleteKey 在空表上覆写丢失全部密钥。
            if (!File.Exists(_filePath)) return new List<ClientKeyInfo>();
            string json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ClientKeyInfo>>(json, JsonOpts) ?? new List<ClientKeyInfo>();
        }
    }

    /// <summary>
    /// 创建新密钥。返回一次性明文（调用方须立即交付租户，不再持久化也不再可重取）与持久化的 KeyInfo。
    /// </summary>
    public (string PlaintextKey, ClientKeyInfo Info) CreateKey(string tenantName, decimal dailyBudgetUsd = 100.0m, int maxQps = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantName);
        lock (_gate)
        {
            var keys = GetAllKeys();
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
            var keys = GetAllKeys();
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
            var keys = GetAllKeys();
            int removed = keys.RemoveAll(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
            if (removed > 0)
            {
                SaveKeysToFile(keys);
                return true;
            }
            return false;
        }
    }

    private void SaveKeysToFile(List<ClientKeyInfo> keys)
    {
        string json = JsonSerializer.Serialize(keys, JsonOpts);
        File.WriteAllText(_filePath, json);
    }
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
    public int MaxQps { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
