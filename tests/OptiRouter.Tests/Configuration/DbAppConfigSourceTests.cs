using OptiRouter.Configuration;
using Xunit;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// DbAppConfigProvider 失败保留语义：运行期 Reload 遇 DB 故障必须保留上一次成功数据，
/// 仅首次加载失败才回落空配置——否则 IOptionsMonitor 立即清空配置，并连带
/// OnChange → Retain(空) 清掉 Thompson/Bandit/配额学习状态。
/// </summary>
public sealed class DbAppConfigSourceTests
{
    [Fact]
    public void Load_FailureAfterSuccessfulLoad_KeepsPreviousData()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"optirouter-cfgsrc-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new AppConfigDbStore(dbPath))
            {
                store.SaveDocument(AppConfigDbStore.RoutingScope, "{\"defaultTier\":\"frontier\"}");
            }

            var provider = new DbAppConfigProvider(dbPath);
            provider.Load();
            Assert.True(provider.TryGet("OptiRouter:Routing:DefaultTier", out var value));
            Assert.Equal("frontier", value);

            // 制造加载失败：把 DB 文件路径替换为目录（SQLite 无法打开）。
            File.Delete(dbPath);
            Directory.CreateDirectory(dbPath);
            provider.Load();

            // 关键断言：旧数据保留，而不是被清空回默认。
            Assert.True(provider.TryGet("OptiRouter:Routing:DefaultTier", out var kept));
            Assert.Equal("frontier", kept);
        }
        finally
        {
            if (Directory.Exists(dbPath))
                Directory.Delete(dbPath, true);
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                string path = dbPath + suffix;
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }
}
