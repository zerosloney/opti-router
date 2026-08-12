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
