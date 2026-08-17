using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OptiRouter.Configuration;

namespace OptiRouter.Tests.Configuration;

public sealed class ModelsConfigServiceTests
{
    [Fact]
    public async Task ConcurrentUpserts_DoNotLoseDistinctModels()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        using var service = CreateService(path);

        var models = Enumerable.Range(0, 24)
            .Select(i => CreateModel($"model-{i}"))
            .ToArray();

        await Task.WhenAll(models.Select(model => Task.Run(() => service.UpsertModel(model))));

        var stored = service.LoadModels();
        Assert.Equal(models.Length, stored.Count);
        Assert.Equal(
            models.Select(model => model.Name).OrderBy(name => name),
            stored.Select(model => model.Name).OrderBy(name => name));
        AssertValidJson(path);

        service.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ConcurrentDeleteAndUpsert_PreservesBothOperations()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        using var service = CreateService(path);
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
        AssertValidJson(path);

        service.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void FailedAtomicReplacement_LeavesPreviousFileAndCleansTemporaryFile()
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        File.WriteAllText(path, SerializeModels(CreateModel("original")));
        using var service = CreateService(path);
        string original = File.ReadAllText(path);
        service.AtomicReplaceHook = (_, _) => throw new IOException("injected replacement failure");

        Assert.Throws<IOException>(() => service.UpsertModel(CreateModel("replacement")));

        Assert.Equal(original, File.ReadAllText(path));
        Assert.Equal(new[] { "original" }, service.LoadModels().Select(model => model.Name));
        Assert.Empty(Directory.EnumerateFiles(directory, ".models-config.json.*.tmp"));

        service.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Upsert_InvalidJsonDoesNotOverwriteOriginalFile()
        => AssertInvalidJsonMutationPreservesFile(service => service.UpsertModel(CreateModel("replacement")));

    [Fact]
    public void Delete_InvalidJsonDoesNotOverwriteOriginalFile()
        => AssertInvalidJsonMutationPreservesFile(service => service.DeleteModel("existing"));

    [Fact]
    public void LoadModels_WithEnvPrefix_ResolvesEnvironmentVariable()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        Environment.SetEnvironmentVariable("TEST_API_KEY_1", "resolved-key-from-env");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_API_KEY_1";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act
            var loaded = service.LoadModels();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("resolved-key-from-env", loaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_API_KEY_1", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadModels_WithEnvPrefix_VariableNotSet_ReturnsEmpty()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");

        // Ensure environment variable is not set
        Environment.SetEnvironmentVariable("TEST_NONEXISTENT_VAR", null);

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_NONEXISTENT_VAR";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act
            var loaded = service.LoadModels();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("", loaded[0].ApiKey); // Should be empty string when env var is missing
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadModels_WithEnvPrefix_VariableSetToEmpty_ReturnsEmpty()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        Environment.SetEnvironmentVariable("TEST_EMPTY_VAR", "");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_EMPTY_VAR";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act
            var loaded = service.LoadModels();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("", loaded[0].ApiKey); // Should be empty string when env var is empty
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_EMPTY_VAR", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadModels_WithoutEnvPrefix_PreservesOriginalValue()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "sk-plain-api-key";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act
            var loaded = service.LoadModels();

            // Assert
            Assert.Single(loaded);
            Assert.Equal("sk-plain-api-key", loaded[0].ApiKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveModels_PreservesEnvLiteral_WhenKeyUnchanged()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        Environment.SetEnvironmentVariable("TEST_SAVE_ENV_KEY", "resolved-value");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_SAVE_ENV_KEY";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);
            var loaded = service.LoadModels();

            // Act: SaveModels 传入解析后的对象（ApiKey 已是 resolved 值）
            service.SaveModels(loaded);

            // Assert: 文件应仍是 env: 字面量
            var fileContent = File.ReadAllText(path);
            Assert.Contains("env:TEST_SAVE_ENV_KEY", fileContent);
            Assert.DoesNotContain("resolved-value", fileContent);

            // 验证重新加载仍然正确解析
            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("resolved-value", reloaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_SAVE_ENV_KEY", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpsertModel_EmptyApiKey_KeepsEnvLiteral()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        Environment.SetEnvironmentVariable("TEST_UPSERT_ENV_KEY", "original-env-value");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_UPSERT_ENV_KEY";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act: UpsertModel 传入空 ApiKey（保留现有）+ 改 BaseUrl
            var updateModel = CreateModel("test-model");
            updateModel.ApiKey = ""; // 空 ApiKey 应保留现有 env: 引用
            updateModel.BaseUrl = "https://updated.example.com/v1";
            service.UpsertModel(updateModel);

            // Assert: 文件应保留 env: 字面量
            var fileContent = File.ReadAllText(path);
            Assert.Contains("env:TEST_UPSERT_ENV_KEY", fileContent);
            Assert.Contains("https://updated.example.com", fileContent);

            // 验证重新加载仍然正确解析
            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("original-env-value", reloaded[0].ApiKey);
            Assert.Equal("https://updated.example.com/v1", reloaded[0].BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_UPSERT_ENV_KEY", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpsertModel_NewPlaintextKey_WritesPlaintext()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        Environment.SetEnvironmentVariable("TEST_PLAINTEXT_ENV_KEY", "env-value");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "env:TEST_PLAINTEXT_ENV_KEY";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act: UpsertModel 传入新明文 key（不同于环境变量值）
            var updateModel = CreateModel("test-model");
            updateModel.ApiKey = "sk-new-plaintext-key";
            service.UpsertModel(updateModel);

            // Assert: 文件应是明文，不是 env: 引用
            var fileContent = File.ReadAllText(path);
            Assert.Contains("sk-new-plaintext-key", fileContent);
            Assert.DoesNotContain("env:TEST_PLAINTEXT_ENV_KEY", fileContent);

            // 验证重新加载
            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("sk-new-plaintext-key", reloaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_PLAINTEXT_ENV_KEY", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpsertModel_NewEnvReference_PassesThrough()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        Environment.SetEnvironmentVariable("TEST_NEW_ENV_VAR", "new-env-value");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "old-plaintext-key";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);

            // Act: UpsertModel 传新的 env: 引用
            var updateModel = CreateModel("test-model");
            updateModel.ApiKey = "env:TEST_NEW_ENV_VAR";
            service.UpsertModel(updateModel);

            // Assert: 文件应是新的 env: 引用
            var fileContent = File.ReadAllText(path);
            Assert.Contains("env:TEST_NEW_ENV_VAR", fileContent);

            // 验证重新加载正确解析
            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("new-env-value", reloaded[0].ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_NEW_ENV_VAR", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveModels_PlaintextModel_BehaviorUnchanged()
    {
        // Arrange
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");

        try
        {
            var model = CreateModel("test-model");
            model.ApiKey = "sk-always-plaintext";
            File.WriteAllText(path, SerializeModels(model));

            using var service = CreateService(path);
            var loaded = service.LoadModels();

            // Act: SaveModels 传入明文模型
            service.SaveModels(loaded);

            // Assert: 文件应仍是明文（回归测试）
            var fileContent = File.ReadAllText(path);
            Assert.Contains("sk-always-plaintext", fileContent);
            Assert.DoesNotContain("env:", fileContent);

            var reloaded = service.LoadModels();
            Assert.Single(reloaded);
            Assert.Equal("sk-always-plaintext", reloaded[0].ApiKey);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ModelsConfigService CreateService(string path)
    {
        var configuration = new ConfigurationBuilder()
            .Add(new ModelsJsonConfigurationSource { FilePath = path })
            .Build();
        return new ModelsConfigService(path, configuration, NullLogger<ModelsConfigService>.Instance);
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "optirouter-model-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
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

    private static string SerializeModels(params ModelEndpointOptions[] models)
        => JsonSerializer.Serialize(models, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });

    private static void AssertInvalidJsonMutationPreservesFile(Action<ModelsConfigService> mutate)
    {
        string directory = CreateDirectory();
        string path = Path.Combine(directory, "models-config.json");
        const string invalidJson = "{not-json";
        File.WriteAllText(path, invalidJson);

        try
        {
            using (var service = CreateService(path))
            {
                Assert.Throws<JsonException>(() => mutate(service));

                Assert.Equal(invalidJson, File.ReadAllText(path));
                Assert.Empty(Directory.EnumerateFiles(directory, ".models-config.json.*.tmp"));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertValidJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }
}
