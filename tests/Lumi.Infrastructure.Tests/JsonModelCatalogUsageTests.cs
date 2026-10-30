using Lumi.Core.Models;
using Lumi.Core.Usage;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>settings.json 裡的 <c>usage</c> 與模型的 <c>pricing</c>（M23）：預設值、舊檔相容、讀寫與變動通知。</summary>
public sealed class JsonModelCatalogUsageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-usage-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogUsageTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NewFile_UsesTheDefaults()
    {
        var usage = NewCatalog().Current.Usage;

        Assert.Null(usage.MonthlyBudgetUsd);
        Assert.Empty(usage.Muted);
    }

    [Fact]
    public async Task Usage_IsSaved_ReloadedAndAnnounced()
    {
        var catalog = NewCatalog();
        AppSettings? announced = null;
        catalog.Changed += (_, s) => announced = s;

        await catalog.SetUsageAsync(new UsageSettings(25m, [UsageHintIds.BigFile, "bogus"]));

        Assert.Equal(25m, announced?.Usage.MonthlyBudgetUsd);
        var reloaded = NewCatalog().Current.Usage;
        Assert.Equal(25m, reloaded.MonthlyBudgetUsd);
        Assert.Equal([UsageHintIds.BigFile], reloaded.Muted);
    }

    [Fact]
    public async Task ModelPricing_RoundTrips()
    {
        var catalog = NewCatalog();
        await catalog.SaveConnectionAsync(new ConnectionSettings("conn", "連線", ProviderFormat.Anthropic, "https://api.test"), null);
        var pricing = new ModelPricing(3m, 15m, 0.3m, 3.75m, false, PriceSource.Suggested);

        await catalog.SaveModelAsync(new ModelSettings("m1", "conn", "claude-x", "X", 1000, 200_000, true) { Pricing = pricing });

        Assert.Equal(pricing, NewCatalog().Current.Models.Single().Pricing);
    }

    [Fact]
    public async Task OldFile_WithoutPricingOrUsage_LoadsWithDefaults()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            { "version": 1,
              "connections": [ { "id": "conn", "name": "連線", "format": "anthropic", "baseUrl": "https://api.test" } ],
              "models": [ { "id": "m1", "connectionId": "conn", "modelId": "claude-x", "displayName": "X", "maxOutputTokens": 1000, "contextWindow": 200000, "showThinking": true } ] }
            """);

        var current = NewCatalog().Current;

        Assert.Equal(ModelPricing.None, current.Models.Single().Pricing);
        Assert.Null(current.Usage.MonthlyBudgetUsd);
        Assert.Empty(current.Usage.Muted);
    }
}
