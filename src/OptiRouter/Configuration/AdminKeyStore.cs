using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OptiRouter.Security;

namespace OptiRouter.Configuration;

/// <summary>
/// 管理端密钥的数据库层存储：SHA256 哈希存配置库 <c>security</c> scope，appsettings/环境变量
/// 仅作首启种子（密钥不再进代码库——此前 appsettings.json 中的明文 AdminApiKey 已随公开仓库泄露）。
/// 种子优先级：库内已有 &gt; 首启种子源（OptiRouter:AdminApiKey）&gt; 随机生成（明文打一次启动日志）。
/// 生成路径仅适用单实例（多实例各自生成会得到不同密钥）；轮换 = 清空 security scope 后重启。
/// <para>
/// 附加管理身份（最小 RBAC）：独立密钥的低权限管理员身份存 <c>admin-identities</c> scope，
/// 主键恒为 admin、不属于 identities；<see cref="TryResolveRole"/> 统一仲裁出示密钥的角色，
/// 供 /login 与管理端 Bearer 鉴权共用。密钥格式跟随 <see cref="ClientKeyService"/> 租户 key 风格。
/// </para>
/// </summary>
public sealed class AdminKeyStore
{
    private const string AdminKeyHashField = "adminKeyHash";
    private const string IdentitiesScope = "admin-identities";

    /// <summary>身份密钥前缀：与租户 key（opti-key-）同风格、前缀可区分用途（管理面附加身份）。</summary>
    private const string IdentityKeyPrefix = "opti-admin-";

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

    /// <summary>
    /// 仲裁出示密钥的角色：先比主键（恒为 admin），再比附加 identities（用其存储角色）。
    /// 与 <see cref="IsValid"/> 同为常量时间比较；identities 逐一比对到底不提前退出
    /// （口径同 ClientKeyService.AuthorizeRequest），比对耗时不泄露命中位置。未命中返回 false。
    /// </summary>
    public bool TryResolveRole(string? presentedToken, out AdminRole role)
    {
        role = AdminRole.Admin;
        byte[]? stored;
        lock (_gate) stored = _storedHash;
        if (stored is null || string.IsNullOrEmpty(presentedToken)) return false;

        byte[] providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presentedToken));
        if (CryptographicOperations.FixedTimeEquals(stored, providedHash)) return true;

        bool matched = false;
        foreach (var identity in LoadIdentities())
        {
            // 比对完所有候选：无效哈希按全零占位同样参与比较（加载期已拒绝非法哈希入档的场景除外）。
            bool valid = TryDecodeHash(identity.KeyHash, out byte[] identityHash);
            identityHash = valid ? identityHash : new byte[32];
            bool equal = CryptographicOperations.FixedTimeEquals(identityHash, providedHash);
            if (valid && equal && !matched)
            {
                role = identity.ResolvedRole;
                matched = true;
            }
        }
        return matched;
    }

    /// <summary>列出全部附加管理身份（含 KeyHash 供内部比对；对外展示须经 API 层剥除）。</summary>
    public IReadOnlyList<AdminIdentity> ListIdentities()
    {
        lock (_gate) return LoadIdentities();
    }

    /// <summary>
    /// 创建附加管理身份。<paramref name="role"/> 必须是三值之一（非法抛 ArgumentException）；
    /// 明文密钥经 <paramref name="plaintextKey"/> 一次性交出，此后只存哈希、不可重取。
    /// </summary>
    public AdminIdentity AddIdentity(string name, string role, out string plaintextKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!AdminRoles.TryParse(role, out var parsedRole))
            throw new ArgumentException($"Unknown role '{role}'. Valid values: admin, operator, viewer.", nameof(role));

        lock (_gate)
        {
            // 格式跟随 ClientKeyService.Build 的租户 key 风格：{scheme前缀}-{Guid N 高熵}。
            plaintextKey = IdentityKeyPrefix + Guid.NewGuid().ToString("N");
            var identity = new AdminIdentity(
                Id: Guid.NewGuid().ToString("N"),
                Name: name.Trim(),
                KeyHash: HashToken(plaintextKey),
                KeyPrefix: plaintextKey[..12],
                Role: AdminRoles.ToValue(parsedRole),
                CreatedAtUtc: DateTime.UtcNow);

            var identities = new List<AdminIdentity>(LoadIdentities()) { identity };
            _store.SaveDocument(IdentitiesScope, JsonSerializer.Serialize(identities, JsonOpts));
            return identity;
        }
    }

    /// <summary>按 Id 删除附加管理身份；不存在返回 false（映射 API 404）。主键不属于 identities，恒不可删。</summary>
    public bool RemoveIdentity(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var identities = LoadIdentities();
            if (identities.RemoveAll(i => string.Equals(i.Id, id, StringComparison.Ordinal)) == 0)
                return false;
            _store.SaveDocument(IdentitiesScope, JsonSerializer.Serialize(identities, JsonOpts));
            return true;
        }
    }

    // intentional-simple: 低频管理操作，每次整文档覆盖读；多实例下以最后一次写入为准，无行级并发。
    private List<AdminIdentity> LoadIdentities()
    {
        string? json = _store.LoadDocument(IdentitiesScope);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<AdminIdentity>>(json, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return []; // 文档损坏按空身份集处理：附加密钥全部失效，主键登录不受影响。
        }
    }

    private static string HashToken(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    private static bool TryDecodeHash(string value, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        if (value.Length != 64) return false;
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

/// <summary>
/// 附加管理身份（最小 RBAC 的低权限管理员密钥条目）。KeyHash 参与持久化序列化，
/// 但绝不出 API——展示面（GET identities）在端点层剥除，口径与租户 KeyHash 一致。
/// Role 存小写字符串（"admin"/"operator"/"viewer"，口径见 AdminRoles）。
/// </summary>
public sealed record AdminIdentity(
    string Id,
    string Name,
    string KeyHash,
    string KeyPrefix,
    string Role,
    DateTime CreatedAtUtc)
{
    /// <summary>解析后的强类型角色（Role 字段被外部改坏时按 Viewer 兜底，仅供展示）。</summary>
    public AdminRole ResolvedRole => AdminRoles.TryParse(Role, out var role) ? role : AdminRole.Viewer;
}
