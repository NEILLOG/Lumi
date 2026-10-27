using System.Text.Json.Nodes;
using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>22F：settings.json 的 <c>imageGeneration</c>——讀寫、舊檔相容、格式錯誤當作未設定、連線被刪除時清成 null。</summary>
public sealed class JsonModelCatalogImageGenerationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-image-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogImageGenerationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    private static ConnectionSettings Gateway() => new("gateway", "公司 Gateway", ProviderFormat.OpenAICompatible, "https://gateway.example/v1");

    [Fact]
    public void NewFile_IsNotConfigured()
    {
        Assert.Equal(ImageGenerationSettings.Default, NewCatalog().Current.ImageGeneration);
        Assert.False(NewCatalog().Current.ImageGeneration.IsConfigured);
    }

    [Fact]
    public async Task Set_SurvivesReload()
    {
        var catalog = NewCatalog();
        await catalog.SaveConnectionAsync(Gateway(), null);

        await catalog.SetImageGenerationAsync(new ImageGenerationSettings("gateway", "gpt-image-2", "1536x1024"));

        var reloaded = NewCatalog().Current.ImageGeneration;
        Assert.Equal("gateway", reloaded.ConnectionId);
        Assert.Equal("gpt-image-2", reloaded.ModelId);
        Assert.Equal("1536x1024", reloaded.Size);
        Assert.True(reloaded.IsConfigured);
    }

    [Fact]
    public async Task Setting_a_connection_that_does_not_exist_clears_it()
    {
        var catalog = NewCatalog();

        await catalog.SetImageGenerationAsync(new ImageGenerationSettings("no-such", "gpt-image-2"));

        Assert.Null(catalog.Current.ImageGeneration.ConnectionId);
        Assert.Null(NewCatalog().Current.ImageGeneration.ConnectionId);
    }

    [Fact]
    public async Task Deleting_the_connection_makes_the_setting_not_configured_but_keeps_the_model_text()
    {
        var catalog = NewCatalog();
        await catalog.SaveConnectionAsync(Gateway(), null);
        await catalog.SetImageGenerationAsync(new ImageGenerationSettings("gateway", "gpt-image-2"));

        await catalog.DeleteConnectionAsync("gateway");

        var reloaded = NewCatalog().Current.ImageGeneration;
        Assert.Null(reloaded.ConnectionId);
        Assert.Equal("gpt-image-2", reloaded.ModelId);
        Assert.False(reloaded.IsConfigured);
    }

    [Fact]
    public async Task Malformed_block_reads_as_not_configured_and_keeps_the_rest_of_the_file()
    {
        var catalog = NewCatalog();
        await catalog.SaveConnectionAsync(Gateway(), null);
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        root["imageGeneration"] = "不是物件";
        File.WriteAllText(SettingsPath, root.ToJsonString());

        var reloaded = NewCatalog();

        Assert.Equal(ImageGenerationSettings.Default, reloaded.Current.ImageGeneration);
        Assert.Contains(reloaded.Current.Connections, c => c.Id == "gateway");
        Assert.Null(reloaded.LoadProblem);
    }

    [Fact]
    public async Task Unknown_size_is_saved_as_the_default()
    {
        var catalog = NewCatalog();
        await catalog.SaveConnectionAsync(Gateway(), null);

        await catalog.SetImageGenerationAsync(new ImageGenerationSettings("gateway", "m", "4096x4096"));

        Assert.Equal("1024x1024", NewCatalog().Current.ImageGeneration.Size);
    }
}
