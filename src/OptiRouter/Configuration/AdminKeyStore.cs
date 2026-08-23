using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OptiRouter.Configuration;

/// <summary>
/// 管理端密钥的数据库层存储：SHA256 哈希存配置库 <c>security</c> scope，appsettings/环境变量
/// 仅作首启种子（密钥不再进代码库——此前 appsettings.json 中的明文 AdminApiKey 已随公开仓库泄露）。
/// 种子优先级：库内已有 &gt; 首启种子源（OptiRouter:AdminApiKey）&gt; 随机生成（明文打一次启动日志）。
/// 生成路径仅适用单实例（多实例各自生成会得到不同密钥）；轮换 = 清空 security scope 后重启。
/// </summary>
public sealed class AdminKeyStore
{
    private const string AdminKeyHashField = "adminKeyHash";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly AppConfigDbStore _store;
    private readonly ILogger<AdminKeyStore>? _logger;
    private readonly object _gate = new();
    private byte[]? _storedHash;

    public AdminKeyStore(AppConfigDbStore store, IConfiguration configuration, ILogger<AdminKeyStore>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
        EnsureSeeded(configuration);
    }

    /// <summary>
    /// 确保密钥哈希存在（幂等，构造时执行一次）。库内已有直接加载；否则用种子源哈希入库；
    /// 两者皆无时生成随机密钥并在启动日志打印明文一次——操作者从日志取回后登录管理台。
    /// </summary>
    public void EnsureSeeded(IConfiguration configuration)
    {
        lock (_gate)
        {
            if (_storedHash is not null) return;

            byte[]? loaded = LoadStoredHash();
            if (loaded is not null)
            {
                _storedHash = loaded;
                return;
            }

            string? seedKey = configuration["OptiRouter:AdminApiKey"];
            if (!string.IsNullOrWhiteSpace(seedKey))
            {
                _storedHash = SHA256.HashData(Encoding.UTF8.GetBytes(seedKey));
                Persist(_storedHash);
                _logger?.LogInformation("Admin key seeded into config database from OptiRouter:AdminApiKey (config value is now ignored; remove it from settings)");
                return;
            }

            // 无种子源：生成随机密钥。明文仅此一次出现在日志（本机日志目录），
            // 哈希入库；明文丢失只能清空 security scope 重启再生成。
            string generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            _storedHash = SHA256.HashData(Encoding.UTF8.GetBytes(generated));
            Persist(_storedHash);
            _logger?.LogWarning("No admin key configured: generated a random key (shown once). AdminApiKey: {Key}", generated);
        }
    }

    /// <summary>校验出示密钥（常量时间比较，防时序侧信道）。供 /login 与管理端 Bearer 鉴权共用。</summary>
    public bool IsValid(string? providedKey)
    {
        byte[]? stored;
        lock (_gate) stored = _storedHash;
        if (stored is null || string.IsNullOrEmpty(providedKey)) return false;

        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(providedKey));
        return CryptographicOperations.FixedTimeEquals(stored, providedHash);
    }

    private byte[]? LoadStoredHash()
    {
        string? json = _store.LoadDocument("security");
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            string? hex = doc.RootElement.TryGetProperty(AdminKeyHashField, out var field)
                && field.ValueKind == JsonValueKind.String
                ? field.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(hex) || hex.Length != 64) return null;
            return Convert.FromHexString(hex);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Persist(byte[] hash)
    {
        _store.SaveDocument("security", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [AdminKeyHashField] = Convert.ToHexString(hash).ToLowerInvariant()
        }, JsonOpts));
    }
}
