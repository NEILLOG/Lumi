using System.Text;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

public sealed class JsonModelCatalogTests : IDisposable
{
    private const string PlainKey = "sk-ant-PLAINTEXT-MARKER-9876";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-settings-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), _secrets, NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ConnectionSettings OpenAiConnection(string? id = null) =>
        new(id ?? ConnectionSettings.NewId(), "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");

    private static ModelSettings Model(string connectionId, string modelId = "llama3") =>
        new(ModelSettings.NewId(), connectionId, modelId, modelId, 4096, 128_000, false);

    [Fact]
    public void FirstRun_CreatesFile_WithMigratedAnthropicConnection()
    {
        var catalog = NewCatalog();

        Assert.True(File.Exists(SettingsPath));
        Assert.Null(catalog.LoadProblem);
        var connection = Assert.Single(catalog.Current.Connections);
        Assert.Equal(JsonModelCatalog.MigratedConnectionId, connection.Id);
        Assert.Equal(ProviderFormat.Anthropic, connection.Format);
        var model = Assert.Single(catalog.Current.Models);
        Assert.Equal("claude-opus-5-5", model.ModelId);
        Assert.Equal(model.Id, catalog.Current.WorkbenchModelId);
        Assert.Equal(model.Id, catalog.Current.SpriteModelId);
        // M1 的金鑰名稱不變，不用搬
        Assert.Equal("connection/dev-anthropic/api-key", ConnectionSecrets.ApiKey(connection.Id).Name);
    }

    [Fact]
    public async Task Settings_SurviveReload()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);
        var model = Model(connection.Id);
        await catalog.SaveModelAsync(model);
        await catalog.SetWorkbenchModelAsync(model.Id);
        await catalog.SetThemeAsync(ThemePreference.Dark);

        var reloaded = NewCatalog();

        Assert.Equal(catalog.Current.Connections, reloaded.Current.Connections);
        Assert.Equal(catalog.Current.Models, reloaded.Current.Models);
        Assert.Equal(model.Id, reloaded.Current.WorkbenchModelId);
        Assert.Equal(ThemePreference.Dark, reloaded.Current.Theme);
        Assert.Contains("\"dark\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task ApiKey_GoesToSecretStore_FileHasOnlyHint()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();

        await catalog.SaveConnectionAsync(connection, new SecretValue(PlainKey));

        Assert.Equal(PlainKey, _secrets.Items[ConnectionSecrets.ApiKey(connection.Id).Name]);
        Assert.Equal("9876", catalog.Current.Connections.Single(c => c.Id == connection.Id).ApiKeyHint);
        var bytes = File.ReadAllBytes(SettingsPath);
        Assert.DoesNotContain(PlainKey, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SaveWithoutKey_KeepsExistingHint_IgnoresCallerHint()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, new SecretValue(PlainKey));

        await catalog.SaveConnectionAsync(connection with { Name = "改名", ApiKeyHint = "FAKE" }, null);

        var saved = catalog.Current.Connections.Single(c => c.Id == connection.Id);
        Assert.Equal("改名", saved.Name);
        Assert.Equal("9876", saved.ApiKeyHint);
    }

    [Fact]
    public async Task ClearApiKey_RemovesSecretAndHint()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, new SecretValue(PlainKey));

        await catalog.ClearApiKeyAsync(connection.Id);

        Assert.Empty(_secrets.Items);
        Assert.Null(catalog.Current.Connections.Single(c => c.Id == connection.Id).ApiKeyHint);
    }

    [Theory]
    [InlineData("", "https://x.test")]
    [InlineData("名稱", "not a url")]
    [InlineData("名稱", "ftp://x.test")]
    public async Task InvalidConnection_Throws(string name, string baseUrl)
    {
        var catalog = NewCatalog();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            catalog.SaveConnectionAsync(new ConnectionSettings(ConnectionSettings.NewId(), name, ProviderFormat.OpenAICompatible, baseUrl), null));
    }

    [Fact]
    public async Task ConnectionIdThatCannotBeASecretName_Throws()
    {
        var catalog = NewCatalog();

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SaveConnectionAsync(OpenAiConnection("Bad Id"), null));
    }

    [Fact]
    public async Task DeleteConnection_RemovesModelsAndSecret_AndFixesDefaults()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, new SecretValue(PlainKey));
        var model = Model(connection.Id);
        await catalog.SaveModelAsync(model);
        await catalog.SetWorkbenchModelAsync(model.Id);

        await catalog.DeleteConnectionAsync(connection.Id);

        Assert.DoesNotContain(catalog.Current.Connections, c => c.Id == connection.Id);
        Assert.DoesNotContain(catalog.Current.Models, m => m.ConnectionId == connection.Id);
        Assert.Empty(_secrets.Items);
        // 預設模型改回剩下的第一個（搬家建立的 Anthropic 模型）
        Assert.Equal(catalog.Current.Models[0].Id, catalog.Current.WorkbenchModelId);
    }

    [Fact]
    public async Task DeletingEveryModel_LeavesDefaultsNull()
    {
        var catalog = NewCatalog();

        await catalog.DeleteModelAsync(catalog.Current.Models[0].Id);

        Assert.Empty(catalog.Current.Models);
        Assert.Null(catalog.Current.WorkbenchModelId);
        Assert.Null(catalog.Current.SpriteModelId);
        Assert.Null(catalog.Resolve(catalog.Current.WorkbenchModelId));
    }

    [Fact]
    public async Task SaveModel_ForMissingConnection_Throws()
    {
        var catalog = NewCatalog();

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SaveModelAsync(Model("no-such-connection")));
    }

    [Fact]
    public async Task SetWorkbenchModel_Unknown_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewCatalog().SetWorkbenchModelAsync("no-such-model"));
    }

    [Fact]
    public void Resolve_ReturnsModelWithItsConnection()
    {
        var catalog = NewCatalog();

        var resolved = catalog.Resolve(catalog.Current.WorkbenchModelId);

        Assert.NotNull(resolved);
        Assert.Equal(JsonModelCatalog.MigratedConnectionId, resolved.Connection.Id);
        Assert.Null(catalog.Resolve("no-such-model"));
        Assert.Null(catalog.Resolve(null));
    }

    [Fact]
    public void Normalize_DropsOrphanModels_AndRepairsDefaults()
    {
        var connection = OpenAiConnection();
        var good = Model(connection.Id);
        var orphan = Model("missing");
        var settings = new AppSettings([connection], [orphan, good], orphan.Id, "gone", ThemePreference.System);

        var normalized = JsonModelCatalog.Normalize(settings);

        Assert.Equal([good], normalized.Models);
        Assert.Equal(good.Id, normalized.WorkbenchModelId);
        Assert.Equal(good.Id, normalized.SpriteModelId);
    }

    [Fact]
    public async Task Changed_IsRaisedWithNewSettings()
    {
        var catalog = NewCatalog();
        AppSettings? received = null;
        catalog.Changed += (_, s) => received = s;

        await catalog.SetThemeAsync(ThemePreference.Light);

        Assert.NotNull(received);
        Assert.Equal(ThemePreference.Light, received.Theme);
        Assert.Same(catalog.Current, received);
    }

    [Fact]
    public void CorruptFile_IsKeptAsBroken_AndDefaultsAreUsed()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ not json");

        var catalog = NewCatalog();

        Assert.NotNull(catalog.LoadProblem);
        var broken = Assert.Single(Directory.GetFiles(_directory, "settings.json.broken-*"));
        Assert.Equal("{ not json", File.ReadAllText(broken));
        Assert.Single(catalog.Current.Connections);
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task NewerVersion_IsReadOnly_AndFileIsNotTouched()
    {
        Directory.CreateDirectory(_directory);
        var content = $$"""{ "version": {{JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        File.WriteAllText(SettingsPath, content);

        var catalog = NewCatalog();

        Assert.NotNull(catalog.LoadProblem);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SetThemeAsync(ThemePreference.Dark));
        Assert.Equal(content, File.ReadAllText(SettingsPath));
    }
}
