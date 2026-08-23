using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// 管理端密钥的数据库层存储：SHA256 哈希存配置库 security scope，
/// appsettings 仅首启种子源，皆缺时生成随机密钥并打印启动日志一次。
/// </summary>
public sealed class AdminKeyStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"admin-key-store-test-{Guid.NewGuid():N}.db");

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

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
