using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;
using OptiRouter.Security;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// 管理端密钥的数据库层存储：SHA256 哈希存配置库 security scope，
/// appsettings 仅首启种子源，皆缺时生成随机密钥并打印启动日志一次。
/// 另含附加管理身份（最小 RBAC）CRUD 与角色仲裁 TryResolveRole 的行为。
/// </summary>
public sealed class AdminKeyStoreTests : IDisposable
{
    private const string SeedKey = "seed-key-1";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"admin-key-store-test-{Guid.NewGuid():N}.db");

    private AdminKeyStore CreateStore(AppConfigDbStore db) =>
        new(db, Config(("OptiRouter:AdminApiKey", SeedKey)), NullLogger<AdminKeyStore>.Instance);

    private static IConfiguration Config(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void EmptyDatabase_SeedsFromConfigSource()
    {
        using var db = new AppConfigDbStore(_dbPath);
        var store = new AdminKeyStore(db, Config(("OptiRouter:AdminApiKey", "seed-key-1")),
            NullLogger<AdminKeyStore>.Instance);

        Assert.True(store.IsValid("seed-key-1"));
        Assert.False(store.IsValid("wrong-key"));
    }

    [Fact]
    public void ExistingDatabaseHash_WinsOverConfigSource()
    {
        // 首启种子后操作者轮换了库内哈希：配置里的旧值不得再通过校验（库是唯一权威）。
        using (var db = new AppConfigDbStore(_dbPath))
        {
            var _ = new AdminKeyStore(db, Config(("OptiRouter:AdminApiKey", "old-key")),
                NullLogger<AdminKeyStore>.Instance);
        }

        // 直接改写 security scope 为 new-key 的哈希（模拟轮换）。
        string newHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("new-key"))).ToLowerInvariant();
        using (var db = new AppConfigDbStore(_dbPath))
        {
            db.SaveDocument("security", $$"""{"adminKeyHash":"{{newHash}}"}""");

            var store = new AdminKeyStore(db, Config(("OptiRouter:AdminApiKey", "old-key")),
                NullLogger<AdminKeyStore>.Instance);
            Assert.False(store.IsValid("old-key"), "rotated-away key must be rejected");
            Assert.True(store.IsValid("new-key"));
        }
    }

    [Fact]
    public void NoSource_GeneratesRandomKey_LoggedOnce_AndValid()
    {
        using var db = new AppConfigDbStore(_dbPath);
        var logger = new CapturingLogger();

        var store = new AdminKeyStore(db, Config(), logger);

        // 生成的明文只出现在启动日志一次：从日志取回并验证其有效。
        string? logged = logger.Messages.FirstOrDefault(m => m.Contains("AdminApiKey: "));
        Assert.NotNull(logged);
        string generated = logged.Substring(logged.IndexOf("AdminApiKey: ", StringComparison.Ordinal) + "AdminApiKey: ".Length).Trim();
        Assert.NotEmpty(generated);
        Assert.True(store.IsValid(generated));
    }

    private sealed class CapturingLogger : ILogger<AdminKeyStore>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    // ──────────────── 附加管理身份（最小 RBAC）────────────────

    [Fact]
    public void AddIdentity_ReturnsOneTimePlaintext_PersistsHashNotPlaintext()
    {
        using var db = new AppConfigDbStore(_dbPath);
        var store = CreateStore(db);

        var identity = store.AddIdentity("watcher", "viewer", out string plaintext);

        // 明文格式跟随租户 key 风格（{scheme前缀}-{Guid N}）；哈希入库且不等于明文。
        Assert.StartsWith("opti-admin-", plaintext);
        Assert.NotEqual(plaintext, identity.KeyHash);
        Assert.Equal(64, identity.KeyHash.Length); // SHA256 hex 口径与 adminKeyHash 一致
        Assert.Equal(plaintext[..12], identity.KeyPrefix);
        Assert.Equal("viewer", identity.Role); // 角色归一化为小写口径
        Assert.Single(store.ListIdentities(), i => i.Id == identity.Id);

        // 明文可用作 Bearer（角色为 viewer）；主键仍为 admin。
        Assert.True(store.TryResolveRole(plaintext, out var role));
        Assert.Equal(AdminRole.Viewer, role);
        Assert.True(store.TryResolveRole(SeedKey, out var mainRole));
        Assert.Equal(AdminRole.Admin, mainRole);
        Assert.False(store.TryResolveRole("unknown-key", out _));
    }

    [Fact]
    public void AddIdentity_NormalizesValidRoleCase_AndRejectsUnknownRole()
    {
        using var db = new AppConfigDbStore(_dbPath);
        var store = CreateStore(db);

        // 合法角色大小写不敏感，归一化为小写口径。
        var identity = store.AddIdentity("ops", "Operator", out string plaintext);
        Assert.Equal("operator", identity.Role);
        Assert.True(store.TryResolveRole(plaintext, out var role));
        Assert.Equal(AdminRole.Operator, role);

        // role 非法抛 ArgumentException（API 层先行校验，此处防绕过）。
        Assert.Throws<ArgumentException>(() => store.AddIdentity("bad", "root", out _));
        Assert.Throws<ArgumentException>(() => store.AddIdentity("bad", "", out _));
        Assert.Throws<ArgumentException>(() => store.AddIdentity("", "viewer", out _));
    }

    [Fact]
    public void RemoveIdentity_ReturnsFalseForUnknown_AndTrueForExisting()
    {
        using var db = new AppConfigDbStore(_dbPath);
        var store = CreateStore(db);
        var identity = store.AddIdentity("watcher", "viewer", out string plaintext);

        // 不存在 → false（映射 API 404）。
        Assert.False(store.RemoveIdentity("nonexistent-id"));
        Assert.True(store.RemoveIdentity(identity.Id));
        Assert.Empty(store.ListIdentities());

        // 已删除密钥立即失效；重复删除同样 false。
        Assert.False(store.TryResolveRole(plaintext, out _));
        Assert.False(store.RemoveIdentity(identity.Id));
    }

    [Fact]
    public void Identities_SurviveReopen_RoundTripThroughDocument()
    {
        var identity = AddIdentityOnFreshStore();
        using (var db = new AppConfigDbStore(_dbPath))
        {
            var store = CreateStore(db);
            var loaded = Assert.Single(store.ListIdentities());
            Assert.Equal(identity.Id, loaded.Id);
            Assert.Equal(identity.Name, loaded.Name);
            Assert.Equal(identity.KeyHash, loaded.KeyHash);
            Assert.Equal(identity.KeyPrefix, loaded.KeyPrefix);
            Assert.Equal(identity.Role, loaded.Role);
        }

        AdminIdentity AddIdentityOnFreshStore()
        {
            using var db = new AppConfigDbStore(_dbPath);
            return CreateStore(db).AddIdentity("persistent", "operator", out _);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
