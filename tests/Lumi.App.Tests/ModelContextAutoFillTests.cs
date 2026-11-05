using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>既有模型的上下文上限自動調高：只動還是預設值的 OpenAI 相容模型、只調高、不動最大輸出、冪等。</summary>
public sealed class ModelContextAutoFillTests
{
    private const int DefaultContext = 128_000;

    /// <summary>手寫的價目表來源：只提供 <see cref="Current"/>，不會更新。</summary>
    private sealed class FakePrices(PriceTable table) : IPriceTableSource
    {
        public PriceTable Current { get; } = table;

        public Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct) =>
            Task.FromResult(new PriceTableUpdateResult(false, Current.SnapshotDate));
    }

    private static PriceEntry Entry(string key, int? maxIn, int? maxOut = 393_216) =>
        new(key, "openai", 0.3m, 0.6m, null, null, maxIn, maxOut);

    private static FakePrices Table(params PriceEntry[] entries) => new(new PriceTable(entries, new DateOnly(2026, 10, 15)));

    private static ConnectionSettings Gateway { get; } =
        new("gateway", "Gateway", ProviderFormat.OpenAICompatible, "https://gateway.corp.example/v1");

    private static ModelSettings Model(string id, string connectionId, string modelId, int context = DefaultContext) =>
        new(id, connectionId, modelId, modelId, 4096, context, false);

    /// <summary>建一個有 Gateway 連線與指定模型的環境，跑一次自動更新，回傳更新後的模型。</summary>
    private static async Task<ModelSettings> RunWithAsync(Func<SettingsHost, ModelSettings> model, FakePrices prices)
    {
        using var host = new SettingsHost();
        await host.Catalog.SaveConnectionAsync(Gateway, null);
        var m = model(host);
        await host.Catalog.SaveModelAsync(m);

        await ModelContextAutoFill.RunAsync(host.Catalog, prices, CancellationToken.None);

        return host.Catalog.Current.Models.Single(x => x.Id == m.Id);
    }

    [Fact]
    public async Task Exact_RaisesTheContextWindow_AndLeavesMaxOutputAlone()
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "acme-flash"), Table(Entry("acme-flash", 1_000_000)));

        Assert.Equal(1_000_000, saved.ContextWindow);
        Assert.Equal(4096, saved.MaxOutputTokens);   // 最大輸出會直接送給 API，不跟著價目表
    }

    [Fact]
    public async Task DeepSeekFlash_IsTreatedAsCloud_AndGetsTheRealLimit()
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "deepseek-flash"), Table(Entry("deepseek-flash", 1_000_000)));

        Assert.Equal(1_000_000, saved.ContextWindow);
    }

    [Fact]
    public async Task Normalized_RaisesTheContextWindow()
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "openai/acme-flash"), Table(Entry("acme-flash", 1_000_000)));

        Assert.Equal(1_000_000, saved.ContextWindow);
        Assert.Equal(4096, saved.MaxOutputTokens);
    }

    [Theory]
    [InlineData("acme-flsh")]      // 近似：只列候選，不自動帶入
    [InlineData("acme-flash-x")]   // 前綴相符：只列候選
    [InlineData("qqqqqqqq")]       // 找不到
    public async Task CandidatesAndNotFound_AreLeftAlone(string modelId)
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, modelId), Table(Entry("acme-flash", 1_000_000)));

        Assert.Equal(DefaultContext, saved.ContextWindow);
    }

    [Fact]
    public async Task NonDefaultContext_IsLeftAlone()
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "acme-flash", context: 50_000), Table(Entry("acme-flash", 1_000_000)));

        Assert.Equal(50_000, saved.ContextWindow);   // 使用者自己調過的值不動
    }

    [Fact]
    public async Task AnthropicConnection_IsLeftAlone()
    {
        var saved = await RunWithAsync(host => Model("m", host.Catalog.Current.Connections[0].Id, "acme-flash"), Table(Entry("acme-flash", 1_000_000)));

        Assert.Equal(DefaultContext, saved.ContextWindow);
    }

    [Fact]
    public async Task LocalModelName_IsLeftAlone()
    {
        // 地端部署的上限不等於價目表（價目表裡的 llama3 是第三方託管服務的規格）
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "llama3"), Table(Entry("llama3", 1_000_000)));

        Assert.Equal(DefaultContext, saved.ContextWindow);
    }

    [Fact]
    public async Task SmallerTableValue_IsLeftAlone()
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "acme-flash"), Table(Entry("acme-flash", 64_000)));

        Assert.Equal(DefaultContext, saved.ContextWindow);   // 只調高不調低
    }

    [Fact]
    public async Task MissingMaxInputTokens_IsLeftAlone()
    {
        var saved = await RunWithAsync(_ => Model("m", Gateway.Id, "acme-flash"), Table(Entry("acme-flash", null)));

        Assert.Equal(DefaultContext, saved.ContextWindow);
    }

    [Fact]
    public async Task RunningTwice_GivesTheSameResult_AndDoesNotWriteAgain()
    {
        using var host = new SettingsHost();
        await host.Catalog.SaveConnectionAsync(Gateway, null);
        await host.Catalog.SaveModelAsync(Model("m", Gateway.Id, "acme-flash"));
        var prices = Table(Entry("acme-flash", 1_000_000));

        await ModelContextAutoFill.RunAsync(host.Catalog, prices, CancellationToken.None);
        var afterFirst = host.Catalog.Current.Models.Single(m => m.Id == "m");
        var changes = 0;
        host.Catalog.Changed += (_, _) => changes++;
        var writeTime = File.GetLastWriteTimeUtc(host.SettingsPath);

        await ModelContextAutoFill.RunAsync(host.Catalog, prices, CancellationToken.None);

        Assert.Equal(afterFirst, host.Catalog.Current.Models.Single(m => m.Id == "m"));
        Assert.Equal(1_000_000, afterFirst.ContextWindow);
        Assert.Equal(0, changes);   // 第二次沒有任何存檔
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(host.SettingsPath));
    }

    [Fact]
    public async Task OnlyTheMatchingModels_AreUpdated()
    {
        using var host = new SettingsHost();
        await host.Catalog.SaveConnectionAsync(Gateway, null);
        await host.Catalog.SaveModelAsync(Model("hit", Gateway.Id, "acme-flash"));
        await host.Catalog.SaveModelAsync(Model("miss", Gateway.Id, "qqqqqqqq"));

        await ModelContextAutoFill.RunAsync(host.Catalog, Table(Entry("acme-flash", 1_000_000)), CancellationToken.None);

        var models = host.Catalog.Current.Models.ToDictionary(m => m.Id);
        Assert.Equal(1_000_000, models["hit"].ContextWindow);
        Assert.Equal(DefaultContext, models["miss"].ContextWindow);
    }
}
