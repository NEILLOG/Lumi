using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>22B：舊的 Claude 預設連線的一次性清理，以及連線最近一次測試結果（<see cref="ConnectionSettings.LastTest"/>）的存檔規則。</summary>
public sealed class JsonModelCatalogConnectionTests : IDisposable
{
    private const string LegacyKeyName = "connection/dev-anthropic/api-key";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-connection-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog(ISecretStore? secrets = null) =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), secrets ?? _secrets, NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>22B 之前寫出的檔案：舊的 Claude 預設（dev-anthropic）加上一個 Ollama 連線，預設模型是 Claude。</summary>
    private void WriteLegacyFile(bool withClaudeOnly = false)
    {
        Directory.CreateDirectory(_directory);
        var ollama = withClaudeOnly ? "" : """
              { "id": "ollama", "name": "Ollama", "format": "openAICompatible", "baseUrl": "http://localhost:11434/v1" }
            """;
        var llama = withClaudeOnly ? "" : """
              { "id": "llama", "connectionId": "ollama", "modelId": "llama3", "displayName": "Llama 3", "maxOutputTokens": 4096, "contextWindow": 128000, "showThinking": false }
            """;
        var connections = withClaudeOnly ? "" : $",\n{ollama}";
        var models = withClaudeOnly ? "" : $",\n{llama}";
        File.WriteAllText(SettingsPath, $$"""
            {
              "version": 1,
              "connections": [
                { "id": "dev-anthropic", "name": "Anthropic", "format": "anthropic", "baseUrl": "https://api.anthropic.com" }{{connections}}
              ],
              "models": [
                { "id": "claude", "connectionId": "dev-anthropic", "modelId": "claude-opus-5-5", "displayName": "Claude Opus 5.5", "maxOutputTokens": 64000, "contextWindow": 1000000, "showThinking": true }{{models}}
              ],
              "workbenchModelId": "claude",
              "spriteModelId": "claude",
              "theme": "system"
            }
            """);
    }

    private static ConnectionSettings OpenAiConnection(string? id = null) =>
        new(id ?? ConnectionSettings.NewId(), "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");

    private static ModelSettings Model(string connectionId) =>
        new(ModelSettings.NewId(), connectionId, "llama3", "llama3", 4096, 128_000, false);

    private static ConnectionTestRecord Record(bool success) =>
        new(success, new DateTimeOffset(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(8)));

    // ---- 舊的 Claude 預設清理 ----

    [Fact]
    public void LegacyClaudeDefault_WithoutKey_IsRemoved_AndDefaultMovesToTheRest()
    {
        WriteLegacyFile();

        var catalog = NewCatalog();

        var connection = Assert.Single(catalog.Current.Connections);
        Assert.Equal("ollama", connection.Id);
        var model = Assert.Single(catalog.Current.Models);
        Assert.Equal("llama", model.Id);
        Assert.Equal("llama", catalog.Current.WorkbenchModelId);
        Assert.Equal("llama", catalog.Current.SpriteModelId);
        Assert.DoesNotContain("dev-anthropic", File.ReadAllText(SettingsPath));   // 清理結果寫回檔案
    }

    [Fact]
    public void LegacyClaudeDefault_CleanupIsOneTime_AndRepeatable()
    {
        WriteLegacyFile();
        NewCatalog();
        var afterFirst = File.ReadAllText(SettingsPath);

        var second = NewCatalog();

        Assert.Equal(["ollama"], second.Current.Connections.Select(c => c.Id));
        Assert.Equal(afterFirst, File.ReadAllText(SettingsPath));   // 第二次啟動不再改檔案
    }

    [Fact]
    public void LegacyClaudeDefault_WhenItWasTheOnlyConnection_LeavesNoDefault()
    {
        WriteLegacyFile(withClaudeOnly: true);

        var catalog = NewCatalog();

        Assert.Empty(catalog.Current.Connections);
        Assert.Empty(catalog.Current.Models);
        Assert.Null(catalog.Current.WorkbenchModelId);
        Assert.Null(catalog.Current.SpriteModelId);
    }

    [Fact]
    public void LegacyClaudeDefault_WithKey_IsKept()
    {
        WriteLegacyFile();
        _secrets.Items[LegacyKeyName] = "sk-ant-kept";

        var catalog = NewCatalog();

        Assert.Equal(["dev-anthropic", "ollama"], catalog.Current.Connections.Select(c => c.Id));
        Assert.Equal("claude", catalog.Current.WorkbenchModelId);
    }

    [Fact]
    public void LegacyClaudeDefault_WhenKeyLookupFails_IsKept()
    {
        WriteLegacyFile();

        var catalog = NewCatalog(new LockedSecretStore());

        Assert.Contains(catalog.Current.Connections, c => c.Id == "dev-anthropic");
        Assert.Equal("claude", catalog.Current.WorkbenchModelId);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public void NonLegacyConnection_WithoutKey_IsNeverRemoved()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [{ "id": "company", "name": "公司 Gateway", "format": "openAICompatible", "baseUrl": "https://gw.example.test/v1" }],
              "models": [],
              "theme": "system"
            }
            """);

        var catalog = NewCatalog();

        Assert.Equal(["company"], catalog.Current.Connections.Select(c => c.Id));
    }

    // ---- LastTest 存檔 ----

    [Fact]
    public async Task SetConnectionTest_SurvivesReload()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);

        await catalog.SetConnectionTestAsync(connection.Id, Record(success: false));

        var reloaded = NewCatalog();
        Assert.Equal(Record(success: false), reloaded.Current.Connections.Single(c => c.Id == connection.Id).LastTest);
    }

    [Fact]
    public async Task SetConnectionTest_DoesNotRaiseChanged()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);
        var raised = 0;
        catalog.Changed += (_, _) => raised++;

        await catalog.SetConnectionTestAsync(connection.Id, Record(success: true));

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task SetConnectionTest_ForUnknownConnection_IsIgnored()
    {
        var catalog = NewCatalog();

        await catalog.SetConnectionTestAsync("no-such-connection", Record(success: true));

        Assert.Empty(catalog.Current.Connections);
    }

    [Fact]
    public void OldFile_WithoutLastTest_ReadsAsUntested()
    {
        WriteLegacyFile();
        _secrets.Items[LegacyKeyName] = "sk-ant-kept";

        var catalog = NewCatalog();

        Assert.All(catalog.Current.Connections, c => Assert.Null(c.LastTest));
    }

    [Fact]
    public async Task RenameOnly_KeepsLastTest()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);
        await catalog.SetConnectionTestAsync(connection.Id, Record(success: true));

        await catalog.SaveConnectionAsync(connection with { Name = "改名" }, null);

        Assert.Equal(Record(success: true), catalog.Current.Connections.Single(c => c.Id == connection.Id).LastTest);
    }

    [Fact]
    public async Task ChangingBaseUrl_ClearsLastTest()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);
        await catalog.SetConnectionTestAsync(connection.Id, Record(success: true));

        await catalog.SaveConnectionAsync(connection with { BaseUrl = "http://localhost:9999/v1" }, null);

        Assert.Null(catalog.Current.Connections.Single(c => c.Id == connection.Id).LastTest);
    }

    [Fact]
    public async Task ChangingFormat_ClearsLastTest()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);
        await catalog.SetConnectionTestAsync(connection.Id, Record(success: true));

        await catalog.SaveConnectionAsync(connection with { Format = ProviderFormat.Anthropic }, null);

        Assert.Null(catalog.Current.Connections.Single(c => c.Id == connection.Id).LastTest);
    }

    [Fact]
    public async Task NewKey_ClearsLastTest_AndCallerSuppliedLastTestIsIgnored()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, null);
        await catalog.SetConnectionTestAsync(connection.Id, Record(success: false));

        await catalog.SaveConnectionAsync(connection with { LastTest = Record(success: true) }, new SecretValue("sk-new"));

        Assert.Null(catalog.Current.Connections.Single(c => c.Id == connection.Id).LastTest);
    }

    [Fact]
    public async Task ClearApiKey_ClearsLastTest()
    {
        var catalog = NewCatalog();
        var connection = OpenAiConnection();
        await catalog.SaveConnectionAsync(connection, new SecretValue("sk-old"));
        await catalog.SetConnectionTestAsync(connection.Id, Record(success: true));

        await catalog.ClearApiKeyAsync(connection.Id);

        Assert.Null(catalog.Current.Connections.Single(c => c.Id == connection.Id).LastTest);
    }

    /// <summary>金鑰查詢一律丟 <see cref="SecretStoreException"/>（鑰匙圈被鎖住）；其他操作照常。</summary>
    private sealed class LockedSecretStore : ISecretStore
    {
        public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<SecretValue?>(null);
        public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromException<bool>(new SecretStoreException("鑰匙圈目前無法使用"));
    }
}
