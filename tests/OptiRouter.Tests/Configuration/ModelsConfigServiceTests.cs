using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Configuration;

/// <summary>
/// ModelsConfigService 测试：存储后端为 SQLite 配置库（AppConfigDbStore）。
/// 覆盖并发 Upsert/Delete、env: 引用展开与保存时字面量恢复、空 ApiKey 保留、明文写回。
/// </summary>
public sealed class ModelsConfigServiceTests
{
    [Fact]
    public async Task ConcurrentUpserts_DoNotLoseDistinctModels()
    {
        using var store = CreateStore(out var service);

        var models = Enumerable.Range(0, 24)
            .Select(i => CreateModel($"model-{i}"))
            .ToArray();

        await Task.WhenAll(models.Select(model => Task.Run(() => service.UpsertModel(model))));

        var stored = service.LoadModels();
        Assert.Equal(models.Length, stored.Count);
        Assert.Equal(
            models.Select(model => model.Name).OrderBy(name => name),
            stored.Select(model => model.Name).OrderBy(name => name));
    }

    [Fact]
    public async Task ConcurrentDeleteAndUpsert_PreservesBothOperations()
    {
        using var store = CreateStore(out var service);
        service.SaveModels(new[] { CreateModel("keep"), CreateModel("delete-me") });

        using var start = new ManualResetEventSlim(false);
        Task<bool> delete = Task.Run(() =>
        {
            start.Wait();
            return service.DeleteModel("delete-me");
        });
        Task upsert = Task.Run(() =>
        {
            start.Wait();
            service.UpsertModel(CreateModel("added"));
        });
        start.Set();

        Assert.True(await delete);
        await upsert;

        var names = service.LoadModels().Select(model => model.Name).OrderBy(name => name).ToArray();
        Assert.Equal(new[] { "added", "keep" }, names);
    }

    [Fact]
    public void LoadModels_WithEnvPrefix_ResolvesEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable("TEST_API_KEY_1", "resolved-key-from-env");
        try
        {
            using var store = CreateStore(out var service);
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_API_KEY_1";
            store.UpsertModel(model);

            var loaded = service.LoadModels();

            Assert.Single(loaded);
            Assert.Equal("resolved-key-from-env", loaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_API_KEY_1", null);
        }
    }

    [Fact]
    public void LoadModels_WithEnvPrefix_VariableNotSet_ReturnsEmpty()
    {
        Environment.SetEnvironmentVariable("TEST_NONEXISTENT_VAR", null);
        using var store = CreateStore(out var service);
        var model = CreateModel("test-model");
        model.ApiKey = "env:TEST_NONEXISTENT_VAR";
        store.UpsertModel(model);

        var loaded = service.LoadModels();

        Assert.Single(loaded);
        Assert.Equal("", loaded[0].ApiKey);
    }

    [Fact]
    public void LoadModels_WithEnvPrefix_VariableSetToEmpty_ReturnsEmpty()
    {
        Environment.SetEnvironmentVariable("TEST_EMPTY_VAR", "");
        try
        {
            using var store = CreateStore(out var service);
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_EMPTY_VAR";
            store.UpsertModel(model);

            var loaded = service.LoadModels();

            Assert.Single(loaded);
            Assert.Equal("", loaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_EMPTY_VAR", null);
        }
    }

    [Fact]
    public void LoadModels_WithoutEnvPrefix_PreservesOriginalValue()
    {
        using var store = CreateStore(out var service);
        var model = CreateModel("test-model");
        model.ApiKey = "sk-plain-api-key";
        store.UpsertModel(model);

        var loaded = service.LoadModels();

        Assert.Single(loaded);
        Assert.Equal("sk-plain-api-key", loaded[0].ApiKey);
    }

    [Fact]
    public void SaveModels_PreservesEnvLiteral_WhenKeyUnchanged()
    {
        Environment.SetEnvironmentVariable("TEST_SAVE_ENV_KEY", "resolved-value");
        try
        {
            using var store = CreateStore(out var service);
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_SAVE_ENV_KEY";
            store.UpsertModel(model);

            // 传入解析后的对象（ApiKey 已是 resolved 值）
            var loaded = service.LoadModels();
            service.SaveModels(loaded);

            // 库中仍应是 env: 字面量
            Assert.Equal("env:TEST_SAVE_ENV_KEY", store.GetRawApiKey("test-model"));

            // 重新加载仍然正确解析
            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("resolved-value", reloaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_SAVE_ENV_KEY", null);
        }
    }

    [Fact]
    public void UpsertModel_EmptyApiKey_KeepsEnvLiteral()
    {
        Environment.SetEnvironmentVariable("TEST_UPSERT_ENV_KEY", "original-env-value");
        try
        {
            using var store = CreateStore(out var service);
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_UPSERT_ENV_KEY";
            store.UpsertModel(model);
            service.LoadModels(); // 重建 env 字面量映射

            var updateModel = CreateModel("test-model");
            updateModel.ApiKey = ""; // 空 ApiKey 应保留现有 env: 引用
            updateModel.BaseUrl = "https://updated.example.com/v1";
            service.UpsertModel(updateModel);

            Assert.Equal("env:TEST_UPSERT_ENV_KEY", store.GetRawApiKey("test-model"));

            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("original-env-value", reloaded[0].ApiKey);
            Assert.Equal("https://updated.example.com/v1", reloaded[0].BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_UPSERT_ENV_KEY", null);
        }
    }

    [Fact]
    public void UpsertModel_NewPlaintextKey_WritesPlaintext()
    {
        Environment.SetEnvironmentVariable("TEST_PLAINTEXT_ENV_KEY", "env-value");
        try
        {
            using var store = CreateStore(out var service);
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_PLAINTEXT_ENV_KEY";
            store.UpsertModel(model);
            service.LoadModels(); // 重建 env 字面量映射

            var updateModel = CreateModel("test-model");
            updateModel.ApiKey = "sk-new-plaintext-key";
            service.UpsertModel(updateModel);

            Assert.Equal("sk-new-plaintext-key", store.GetRawApiKey("test-model"));

            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("sk-new-plaintext-key", reloaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_PLAINTEXT_ENV_KEY", null);
        }
    }

    [Fact]
    public void UpsertModel_NewEnvReference_PassesThrough()
    {
        Environment.SetEnvironmentVariable("TEST_NEW_ENV_VAR", "new-env-value");
        try
        {
            using var store = CreateStore(out var service);
            var model = CreateModel("test-model");
            model.ApiKey = "old-plaintext-key";
            store.UpsertModel(model);
            service.LoadModels(); // 重建 env 字面量映射

            var updateModel = CreateModel("test-model");
            updateModel.ApiKey = "env:TEST_NEW_ENV_VAR";
            service.UpsertModel(updateModel);

            Assert.Equal("env:TEST_NEW_ENV_VAR", store.GetRawApiKey("test-model"));

            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("new-env-value", reloaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_NEW_ENV_VAR", null);
        }
    }

    [Fact]
    public void SaveModels_PlaintextModel_BehaviorUnchanged()
    {
        using var store = CreateStore(out var service);
        var model = CreateModel("test-model");
        model.ApiKey = "sk-always-plaintext";
        store.UpsertModel(model);

        var loaded = service.LoadModels();
        service.SaveModels(loaded);

        Assert.Equal("sk-always-plaintext", store.GetRawApiKey("test-model"));

        var reloaded = service.LoadModels();
        Assert.Single(reloaded);
        Assert.Equal("sk-always-plaintext", reloaded[0].ApiKey);
    }

    [Fact]
    public void UpsertModel_InvalidModel_ThrowsAndDoesNotPersist()
    {
        using var store = CreateStore(out var service);

        var invalid = CreateModel("bad-model");
        invalid.MaxContextTokens = -1; // 触发 RouterOptionsValidator 拒绝

        Assert.Throws<ArgumentException>(() => service.UpsertModel(invalid));
        Assert.Empty(store.LoadModelsRaw());
    }

    [Fact]
    public void DeleteModel_UnknownName_ReturnsFalse()
    {
        using var store = CreateStore(out var service);
        Assert.False(service.DeleteModel("does-not-exist"));
    }

    /// <summary>
    /// 2026-08-26 生产事故回归保护：删除模型后其他模型 FallbackChain 残留对它的引用，
    /// 启动期 RouterOptionsValidator 校验失败 → 服务重启崩溃循环。删除必须联动清理。
    /// </summary>
    [Fact]
    public void DeleteModel_PrunesDanglingFallbackChainReferences()
    {
        using var store = CreateStore(out var service);
        var a = CreateModel("model-a");
        a.FallbackChain = new List<string> { "model-b", "model-c" };
        var b = CreateModel("model-b");
        b.FallbackChain = new List<string> { "model-a", "model-c" };
        var c = CreateModel("model-c");
        service.SaveModels(new[] { a, b, c });

        Assert.True(service.DeleteModel("model-c"));

        var reloaded = service.LoadModels();
        Assert.Equal(2, reloaded.Count);
        var reloadedA = reloaded.Single(m => m.Name == "model-a");
        var reloadedB = reloaded.Single(m => m.Name == "model-b");
        Assert.Equal(new[] { "model-b" }, reloadedA.FallbackChain);
        Assert.Equal(new[] { "model-a" }, reloadedB.FallbackChain);
    }

    [Fact]
    public void DeleteModel_RecordsProviderTombstone_ForHistoricalUsageAttribution()
    {
        // 删除后历史审计的供应商归组依据：显式 Provider 与 BaseUrl 推断两种口径都要留痕。
        using var store = CreateStore(out var service);
        var explicitProvider = CreateModel("model-a");
        explicitProvider.Provider = "acme";
        var inferredProvider = CreateModel("model-b");
        inferredProvider.BaseUrl = "https://api.deepseek.com/v1";
        service.SaveModels(new[] { explicitProvider, inferredProvider });

        service.DeleteModel("model-a");
        service.DeleteModel("model-b");

        var tombstones = service.LoadProviderTombstones();
        Assert.Equal("acme", tombstones["model-a"]);
        Assert.Equal("deepseek", tombstones["model-b"]);
    }

    [Fact]
    public void SaveModels_RemovedModels_GetProviderTombstones()
    {
        // 管理台批量保存少掉模型（整体替换路径）与逐个删除同样留痕。
        using var store = CreateStore(out var service);
        var a = CreateModel("model-a");
        a.Provider = "acme";
        service.SaveModels(new[] { a, CreateModel("model-b") });

        var kept = service.LoadModels().Where(m => m.Name != "model-a").ToList();
        service.SaveModels(kept);

        Assert.Equal("acme", service.LoadProviderTombstones()["model-a"]);
    }

    [Fact]
    public void SaveModels_RemovingModel_PrunesDanglingFallbackChainReferences()
    {
        // 整体替换少掉模型（管理台批量保存路径）同样联动收敛，不留悬空引用到下次重启。
        using var store = CreateStore(out var service);
        var a = CreateModel("model-a");
        a.FallbackChain = new List<string> { "model-b", "model-c" };
        service.SaveModels(new[] { a, CreateModel("model-b"), CreateModel("model-c") });

        // 整体保存时去掉 model-c（模拟删除），a 的链引用需被清理。
        var kept = service.LoadModels().Where(m => m.Name != "model-c").ToList();
        service.SaveModels(kept);

        var reloaded = service.LoadModels();
        var reloadedA = reloaded.Single(m => m.Name == "model-a");
        Assert.Equal(new[] { "model-b" }, reloadedA.FallbackChain);
    }

    [Fact]
    public void IdOnlyModels_GetStableRowKeys()
    {
        using var store = CreateStore(out var service);
        var model = CreateModel("");
        model.Id = "upstream-model-id";
        model.BaseUrl = "https://example.test/v1";
        store.UpsertModel(model);

        var loaded = service.LoadModels();
        Assert.Single(loaded);
        Assert.Equal("", loaded[0].Name);
        Assert.Equal("upstream-model-id", loaded[0].Id);
    }

    private static AppConfigDbStore CreateStore(out ModelsConfigService service)
    {
        string dbPath = Path.Combine(
            Path.GetTempPath(),
            "optirouter-model-service-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new AppConfigDbStore(dbPath);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        service = new ModelsConfigService(store, configuration, NullLogger<ModelsConfigService>.Instance);
        return store;
    }

    private static ModelEndpointOptions CreateModel(string name) => new()
    {
        Name = name,
        BaseUrl = "https://example.test/v1",
        MaxContextTokens = 8192,
        InputPricePerMillion = 1,
        OutputPricePerMillion = 2,
        TimeoutSeconds = 30,
        MaxRetries = 0,
        Enabled = true
    };
}
