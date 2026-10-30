using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Models;
using Lumi.Core.Usage;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.App.Tests;

/// <summary>M23 23F：升級後第一次啟動的單價自動帶入（只做一次、只動沒有單價的、本機設為免費、近似與找不到不動）。</summary>
public sealed class PricingAutoFillTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-autofill-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConversationStore _meta;

    public PricingAutoFillTests()
    {
        Directory.CreateDirectory(_directory);
        _meta = new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = Path.Combine(_directory, "lumi.db") }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>手寫的價目表來源：只提供 <see cref="Current"/>，不會更新。</summary>
    private sealed class FakePrices(PriceTable table) : IPriceTableSource
    {
        public PriceTable Current { get; } = table;

        public Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct) =>
            Task.FromResult(new PriceTableUpdateResult(false, Current.SnapshotDate));
    }

    private static FakePrices Table() => new(new PriceTable(
    [
        new PriceEntry("claude-sonnet-4-5", "anthropic", 3m, 15m, 0.3m, 3.75m, 200_000, 32_000),
        new PriceEntry("claude-haiku-4-5", "anthropic", 1m, 5m, null, null, null, null),
        new PriceEntry("azure/gpt-4o", "azure", 2.5m, 10m, null, null, null, null),
        new PriceEntry("openai/gpt-4o", "openai", 2.5m, 10m, null, null, null, null),
    ], new DateOnly(2026, 10, 15)));

    [Fact]
    public async Task Run_FillsExact_AndLeavesLocalhostCandidatesNotFoundAndConfirmedAlone()
    {
        using var host = new SettingsHost();
        var anthropicId = host.Catalog.Current.Connections[0].Id;
        var ollama = new ConnectionSettings("ollama", "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");
        var gateway = new ConnectionSettings("gateway", "Gateway", ProviderFormat.OpenAICompatible, "https://gateway.corp.example/v1");
        await host.Catalog.SaveConnectionAsync(ollama, null);
        await host.Catalog.SaveConnectionAsync(gateway, null);
        var exact = new ModelSettings("m-exact", anthropicId, "claude-sonnet-4-5", "Sonnet", 4096, 50_000, false);
        var local = new ModelSettings("m-local", ollama.Id, "llama3", "Llama 3", 4096, 128_000, false);
        var candidate = new ModelSettings("m-candidate", gateway.Id, "gpt-4o-prod", "GPT", 4096, 128_000, false);
        var notFound = new ModelSettings("m-none", gateway.Id, "qqqqqqqq", "Unknown", 4096, 128_000, false);
        var confirmed = new ModelSettings("m-confirmed", anthropicId, "claude-haiku-4-5", "Haiku", 4096, 200_000, false)
        {
            Pricing = new ModelPricing(9m, 9m, null, null, false, PriceSource.Confirmed),
        };
        foreach (var model in new[] { exact, local, candidate, notFound, confirmed })
            await host.Catalog.SaveModelAsync(model);

        await PricingAutoFill.RunAsync(host.Catalog, Table(), _meta, CancellationToken.None);

        var models = host.Catalog.Current.Models.ToDictionary(m => m.Id);
        Assert.Equal(new ModelPricing(3m, 15m, 0.3m, 3.75m, false, PriceSource.Suggested), models["m-exact"].Pricing);
        Assert.Equal(50_000, models["m-exact"].ContextWindow);   // 上下文上限與最大輸出不動
        Assert.Equal(4096, models["m-exact"].MaxOutputTokens);
        Assert.Equal(ModelPricing.None, models["m-local"].Pricing);   // 不從網址猜地端：沒有相符的就不動，免費由使用者自己勾選
        Assert.Equal(ModelPricing.None, models["m-candidate"].Pricing);
        Assert.Equal(ModelPricing.None, models["m-none"].Pricing);
        Assert.Equal(new ModelPricing(9m, 9m, null, null, false, PriceSource.Confirmed), models["m-confirmed"].Pricing);
        Assert.Equal("1", await _meta.GetMetaAsync(PricingAutoFill.DoneKey));
    }

    [Fact]
    public async Task Run_OnlyOnce_WhenTheFlagIsAlreadySet()
    {
        using var host = new SettingsHost();
        var anthropicId = host.Catalog.Current.Connections[0].Id;
        await PricingAutoFill.RunAsync(host.Catalog, Table(), _meta, CancellationToken.None);

        // 旗標已經是 1：之後新增的模型不會被這一次的比對帶入（之後的新增由表單處理）
        await host.Catalog.SaveModelAsync(new ModelSettings("m-later", anthropicId, "claude-haiku-4-5", "Haiku", 4096, 200_000, false));
        await PricingAutoFill.RunAsync(host.Catalog, Table(), _meta, CancellationToken.None);

        Assert.Equal(ModelPricing.None, host.Catalog.Current.Models.Single(m => m.Id == "m-later").Pricing);
    }

    [Fact]
    public async Task Run_WithNoMatchingModels_StillMarksTheFlagDone()
    {
        using var host = new SettingsHost();

        await PricingAutoFill.RunAsync(host.Catalog, Table(), _meta, CancellationToken.None);

        // 預設模型 claude-opus-5-5 不在價目表裡：不動
        Assert.Equal(ModelPricing.None, host.Catalog.Current.Models.Single().Pricing);
        Assert.Equal("1", await _meta.GetMetaAsync(PricingAutoFill.DoneKey));
    }
}
