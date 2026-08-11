using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Configuration;

/// <summary>
/// 客户端 API Key 与多租户配额管理服务。持久化存储至 client-keys.json。
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
                var defaults = new List<ClientKeyInfo>
                {
                    new()
                    {
                        Key = "opti-key-default-admin",
                        TenantName = "默认核心管理租户",
                        DailyBudgetUsd = 1000.0m,
                        MaxQps = 100,
                        Enabled = true,
                        CreatedAt = DateTime.UtcNow
                    },
                    new()
                    {
                        Key = "opti-key-finance-app",
                        TenantName = "财务部 App 端点",
                        DailyBudgetUsd = 50.0m,
                        MaxQps = 20,
                        Enabled = true,
                        CreatedAt = DateTime.UtcNow
                    }
                };
                SaveKeysToFile(defaults);
            }
        }
    }

    public List<ClientKeyInfo> GetAllKeys()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_filePath)) return new List<ClientKeyInfo>();
                string json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<List<ClientKeyInfo>>(json, JsonOpts) ?? new List<ClientKeyInfo>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read client-keys.json");
                return new List<ClientKeyInfo>();
            }
        }
    }

    public ClientKeyInfo CreateKey(string tenantName, decimal dailyBudgetUsd = 100.0m, int maxQps = 50)
    {
        lock (_gate)
        {
            var keys = GetAllKeys();
            var newKey = new ClientKeyInfo
            {
                Key = $"opti-key-{Guid.NewGuid().ToString("N")[..12]}",
                TenantName = tenantName.Trim(),
                DailyBudgetUsd = Math.Max(0, dailyBudgetUsd),
                MaxQps = Math.Max(1, maxQps),
                Enabled = true,
                CreatedAt = DateTime.UtcNow
            };
            keys.Add(newKey);
            SaveKeysToFile(keys);
            return newKey;
        }
    }

    public bool UpdateKey(string key, bool? enabled, decimal? dailyBudgetUsd, int? maxQps)
    {
        lock (_gate)
        {
            var keys = GetAllKeys();
            var item = keys.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.Ordinal));
            if (item is null) return false;

            if (enabled.HasValue) item.Enabled = enabled.Value;
            if (dailyBudgetUsd.HasValue && dailyBudgetUsd.Value >= 0) item.DailyBudgetUsd = dailyBudgetUsd.Value;
            if (maxQps.HasValue && maxQps.Value > 0) item.MaxQps = maxQps.Value;

            SaveKeysToFile(keys);
            return true;
        }
    }

    public bool DeleteKey(string key)
    {
        lock (_gate)
        {
            var keys = GetAllKeys();
            int removed = keys.RemoveAll(k => string.Equals(k.Key, key, StringComparison.Ordinal));
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
    public required string Key { get; set; }
    public required string TenantName { get; set; }
    public decimal DailyBudgetUsd { get; set; } = 100.0m;
    public decimal DailySpendUsd { get; set; } = 0.0m;
    public int MaxQps { get; set; } = 50;
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
