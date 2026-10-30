using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>M23 23F：模型設定的單價段落（比對、候選、套用、還原、錯誤、更新價目表），以及 <see cref="ModelsViewModel"/> 與單價的連動。</summary>
public sealed class ModelPricingViewModelTests
{
    private static readonly DateOnly OldDate = new(2026, 10, 15);
    private static readonly DateOnly NewDate = new(2026, 10, 30);

    private static readonly ConnectionSettings Anthropic = new("anthropic-conn", "Anthropic", ProviderFormat.Anthropic, "https://api.anthropic.com");
    private static readonly ConnectionSettings Gateway = new("gateway-conn", "公司 Gateway", ProviderFormat.OpenAICompatible, "https://gateway.corp.example/v1");
    private static readonly ConnectionSettings Ollama = new("ollama-conn", "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");

    private static PriceEntry Entry(string key, string? provider, decimal input, decimal output, decimal? cacheRead = null, decimal? cacheWrite = null,
        int? maxIn = null, int? maxOut = null) => new(key, provider, input, output, cacheRead, cacheWrite, maxIn, maxOut);

    private static PriceEntry[] DefaultEntries() =>
    [
        Entry("claude-sonnet-4-5", "anthropic", 3m, 15m, 0.3m, 3.75m, 200_000, 32_000),
        Entry("azure/gpt-4o", "azure", 2.5m, 10m),
        Entry("openai/gpt-4o", "openai", 2.5m, 10m),
    ];

    private static string Summary(string input, string output) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Pricing_SummaryFormat, input, output);

    /// <summary>手寫的價目表來源：<see cref="NextResult"/> 決定下一次更新的結果，更新成功時自己換掉 <see cref="Current"/>。</summary>
    private sealed class FakePrices(PriceTable table) : IPriceTableSource
    {
        public PriceTable Current { get; set; } = table;

        public Func<PriceTableUpdateResult> NextResult { get; set; } = () => new PriceTableUpdateResult(false, OldDate);

        public Exception? Throws { get; set; }

        public Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct) =>
            Throws is { } error ? Task.FromException<PriceTableUpdateResult>(error) : Task.FromResult(NextResult());
    }

    private static FakePrices Prices(DateOnly? date = null, params PriceEntry[] entries) =>
        new(new PriceTable(entries.Length == 0 ? DefaultEntries() : entries, date ?? OldDate));

    // ---- 比對與畫面狀態 ----

    [Fact]
    public void Exact_AutoFillsAndShowsSummary()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);

        Assert.True(vm.ShowSummary);
        Assert.False(vm.ShowFields);
        Assert.False(vm.ShowCandidates);
        Assert.True(vm.IsAutoFilled);
        Assert.Equal(Summary("3.00", "15.00"), vm.SummaryText);
        Assert.Equal("0.30", vm.CacheReadText);
        Assert.Equal(new ModelPricing(3m, 15m, 0.3m, 3.75m, false, PriceSource.Suggested), vm.ToPricing());
    }

    [Fact]
    public void Normalized_AutoFills_WhenProviderPrefixAndDateSuffixAreDifferent()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(ModelPricing.None, "anthropic/claude-sonnet-4-5-20250929", Anthropic);

        Assert.True(vm.ShowSummary);
        Assert.True(vm.IsAutoFilled);
        Assert.Equal("3.00", vm.InputText);
    }

    [Fact]
    public void Candidates_AreListedAndNotAppliedAutomatically()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(ModelPricing.None, "gpt-4o-prod", Gateway);

        Assert.True(vm.ShowCandidates);
        Assert.False(vm.ShowFields);
        Assert.False(vm.ShowSummary);
        Assert.Equal("openai/gpt-4o", vm.Candidates[0].ModelId);   // 連線是一般 OpenAI 相容：openai 優先
        Assert.Contains("azure/gpt-4o", vm.Candidates.Select(c => c.ModelId));
        Assert.Equal(ModelPricing.None, vm.ToPricing());
    }

    [Fact]
    public void ApplyCandidate_SelectsOneAndConfirmsPrice()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "gpt-4o-prod", Gateway);
        Assert.False(vm.ApplyCandidateCommand.CanExecute(null));

        vm.Candidates[0].IsSelected = true;
        vm.Candidates[1].IsSelected = true;   // 單選：第二筆選取後第一筆自動取消
        Assert.False(vm.Candidates[0].IsSelected);
        Assert.True(vm.ApplyCandidateCommand.CanExecute(null));

        vm.ApplyCandidateCommand.Execute(null);

        Assert.True(vm.ShowSummary);
        Assert.False(vm.IsAutoFilled);
        Assert.Equal(new ModelPricing(2.5m, 10m, null, null, false, PriceSource.Confirmed), vm.ToPricing());
    }

    [Fact]
    public void Manual_ShowsEmptyFields_AndTypingConfirmsPrice()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "gpt-4o-prod", Gateway);

        vm.ManualCommand.Execute(null);

        Assert.False(vm.ShowCandidates);
        Assert.True(vm.ShowFields);
        Assert.Equal("", vm.InputText);
        vm.InputText = "1";
        vm.OutputText = "2";
        Assert.Equal(new ModelPricing(1m, 2m, null, null, false, PriceSource.Confirmed), vm.ToPricing());
    }

    [Fact]
    public void NotFound_ShowsEmptyFields_AndStaysUnset()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(ModelPricing.None, "qqqqqqqq", Anthropic);

        Assert.True(vm.ShowFields);
        Assert.True(vm.ShowNotFound);
        Assert.False(vm.ShowCandidates);
        Assert.False(vm.ShowSummary);
        Assert.False(vm.CanRestore);
        Assert.Equal(ModelPricing.None, vm.ToPricing());
    }

    [Fact]
    public void Local_IsFreeAndShowsNoFields()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(ModelPricing.None, "llama3", Ollama);

        Assert.True(vm.IsLocal);
        Assert.False(vm.ShowSummary);
        Assert.False(vm.ShowFields);
        Assert.False(vm.ShowCandidates);
        var pricing = vm.ToPricing();
        Assert.True(pricing.IsFree);
        Assert.True(pricing.IsSet);
    }

    // ---- 編輯、還原、錯誤 ----

    [Fact]
    public void EditingAutoFilledPrice_ConfirmsIt()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);

        vm.EditCommand.Execute(null);
        Assert.True(vm.ShowFields);
        Assert.False(vm.ShowSummary);
        vm.InputText = "4";

        Assert.Equal(PriceSource.Confirmed, vm.ToPricing().Source);
        Assert.Equal(4m, vm.ToPricing().InputPerMTok);
    }

    [Fact]
    public void Restore_GoesBackToTheAutoFilledPrice()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);
        vm.EditCommand.Execute(null);
        vm.InputText = "4";
        Assert.True(vm.CanRestore);

        vm.RestoreCommand.Execute(null);

        Assert.Equal("3.00", vm.InputText);
        Assert.True(vm.ShowSummary);
        Assert.True(vm.IsAutoFilled);
        Assert.Equal(PriceSource.Suggested, vm.ToPricing().Source);
    }

    [Fact]
    public void InvalidNumbers_ShowErrors_AndBlankIsAllowed()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);
        vm.EditCommand.Execute(null);

        vm.InputText = "-1";
        Assert.Equal(Strings.Pricing_Invalid, vm.InputError);
        Assert.True(vm.HasError);

        vm.InputText = "abc";
        Assert.Equal(Strings.Pricing_Invalid, vm.InputError);

        vm.InputText = "   ";
        Assert.Null(vm.InputError);
        Assert.False(vm.HasError);

        vm.CacheWriteText = "x";
        Assert.Equal(Strings.Pricing_Invalid, vm.CacheWriteError);
        Assert.True(vm.ShowAdvanced);   // 錯誤在收合的快取欄位裡：自動展開
    }

    // ---- 重新比對 ----

    [Fact]
    public void ConfirmedPrice_SurvivesConnectionChange_ButNotModelChange()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);
        vm.EditCommand.Execute(null);
        vm.InputText = "4";

        var sameModel = vm.Rematch("claude-sonnet-4-5", Gateway);
        Assert.Null(sameModel);
        Assert.Equal("4", vm.InputText);
        Assert.Equal(PriceSource.Confirmed, vm.ToPricing().Source);

        vm.Rematch("qqqqqqqq", Gateway);
        Assert.Equal("", vm.InputText);
        Assert.Equal(PriceSource.None, vm.ToPricing().Source);
    }

    [Fact]
    public void Rematch_ReturnsTheMatch_OnlyWhenTheModelIdChanges()
    {
        var vm = new ModelPricingViewModel(Prices());
        vm.Load(ModelPricing.None, "", Anthropic);

        var entry = vm.Rematch("claude-sonnet-4-5", Anthropic);
        Assert.NotNull(entry);
        Assert.Equal(200_000, entry.MaxInputTokens);

        Assert.Null(vm.Rematch("claude-sonnet-4-5", Gateway));
    }

    [Fact]
    public void Load_KeepsStoredConfirmedPrice_EvenWhenTheTableDiffers()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(new ModelPricing(7m, 8m, null, null, false, PriceSource.Confirmed), "claude-sonnet-4-5", Anthropic);

        Assert.Equal("7.00", vm.InputText);
        Assert.Equal(PriceSource.Confirmed, vm.ToPricing().Source);
    }

    [Fact]
    public void Load_RefreshesStoredSuggestedPrice_FromTheTable()
    {
        var vm = new ModelPricingViewModel(Prices());

        vm.Load(new ModelPricing(1m, 1m, null, null, false, PriceSource.Suggested), "claude-sonnet-4-5", Anthropic);

        Assert.Equal("3.00", vm.InputText);
        Assert.True(vm.IsAutoFilled);
    }

    [Theory]
    [InlineData(3, "3.00")]
    [InlineData(0.075, "0.075")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00005, "0.0001")]
    [InlineData(1234.5, "1234.50")]
    public void FormatPrice_TwoToFourDecimals(double value, string expected) =>
        Assert.Equal(expected, ModelPricingViewModel.FormatPrice((decimal)value));

    // ---- 更新價目表 ----

    [Fact]
    public async Task UpdateTable_Success_RematchesSuggestedPrice_AndShowsTheNewDate()
    {
        var prices = Prices();
        var vm = new ModelPricingViewModel(prices);
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);
        prices.NextResult = () =>
        {
            prices.Current = new PriceTable([Entry("claude-sonnet-4-5", "anthropic", 4m, 16m, maxIn: 200_000, maxOut: 32_000)], NewDate);
            return new PriceTableUpdateResult(true, NewDate);
        };

        await vm.UpdateTableCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Pricing_UpdatedFormat, "2026-10-30"), vm.UpdateStatusText);
        Assert.Equal("4.00", vm.InputText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Pricing_TableDateFormat, "2026-10-30"), vm.TableDateText);
        Assert.False(vm.IsUpdating);
    }

    [Fact]
    public async Task UpdateTable_Failure_KeepsTheCurrentTable()
    {
        var prices = Prices();
        var vm = new ModelPricingViewModel(prices);
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);

        await vm.UpdateTableCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Pricing_UpdateFailed, vm.UpdateStatusText);
        Assert.Equal("3.00", vm.InputText);
        Assert.False(vm.IsUpdating);
    }

    [Fact]
    public async Task UpdateTable_Exception_ShowsFailure_AndResetsBusyState()
    {
        var prices = Prices();
        prices.Throws = new IOException("disk full");
        var vm = new ModelPricingViewModel(prices);
        vm.Load(ModelPricing.None, "claude-sonnet-4-5", Anthropic);

        await vm.UpdateTableCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Pricing_UpdateFailed, vm.UpdateStatusText);
        Assert.False(vm.IsUpdating);
        Assert.True(vm.UpdateTableCommand.CanExecute(null));
    }

    [Fact]
    public void TableDate_ShowsTheSnapshotDate()
    {
        var vm = new ModelPricingViewModel(Prices(new DateOnly(2026, 10, 15)));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Pricing_TableDateFormat, "2026-10-15"), vm.TableDateText);
    }
}

/// <summary>M23 23F：<see cref="ModelsViewModel"/> 的單價連動（新增時帶入上下文上限、儲存、髒檢查、驗證、沒有價目表時的行為）。</summary>
public sealed class ModelsViewModelPricingTests
{
    private sealed class FakePrices : IPriceTableSource
    {
        public PriceTable Current { get; } = new(
        [
            new PriceEntry("claude-sonnet-4-5", "anthropic", 3m, 15m, 0.3m, 3.75m, 200_000, 32_000),
            new PriceEntry("azure/gpt-4o", "azure", 2.5m, 10m, null, null, null, null),
        ], new DateOnly(2026, 10, 15));

        public Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct) => Task.FromResult(new PriceTableUpdateResult(false, Current.SnapshotDate));
    }

    private static ModelsViewModel NewModels(SettingsHost host, IPriceTableSource? prices) =>
        new(host.Catalog, host.Tester, host.Status, host.Confirm, new SyncUiDispatcher(), prices);

    [Fact]
    public async Task NewModel_ContextAndMaxOutput_FollowAnExactMatch()
    {
        using var host = new SettingsHost();
        var vm = NewModels(host, new FakePrices());

        await vm.StartNewAsync(null);
        vm.ModelId = "claude-sonnet-4-5";

        Assert.Equal(200_000m, vm.ContextWindow);
        Assert.Equal(32_000m, vm.MaxOutputTokens);
        Assert.Equal(PriceSource.Suggested, vm.Pricing!.ToPricing().Source);
    }

    [Fact]
    public async Task EditExisting_DoesNotOverwriteContextOrMaxOutput()
    {
        using var host = new SettingsHost();
        var connectionId = host.Catalog.Current.Connections[0].Id;
        var model = new ModelSettings(ModelSettings.NewId(), connectionId, "claude-sonnet-4-5", "Sonnet", 4096, 50_000, false);
        await host.Catalog.SaveModelAsync(model);
        var vm = NewModels(host, new FakePrices());

        await vm.SelectAsync(vm.Items.Single(i => i.Id == model.Id));
        vm.ModelId = "anthropic/claude-sonnet-4-5";

        Assert.Equal(50_000m, vm.ContextWindow);
        Assert.Equal(4096m, vm.MaxOutputTokens);
    }

    [Fact]
    public async Task Save_NewModel_PersistsTheSuggestedPrice()
    {
        using var host = new SettingsHost();
        var vm = NewModels(host, new FakePrices());
        await vm.StartNewAsync(null);
        vm.ModelId = "claude-sonnet-4-5";

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = host.Catalog.Current.Models.Single(m => m.ModelId == "claude-sonnet-4-5");
        Assert.Equal(new ModelPricing(3m, 15m, 0.3m, 3.75m, false, PriceSource.Suggested), saved.Pricing);
        Assert.Equal(200_000, saved.ContextWindow);
    }

    [Fact]
    public async Task OpeningAModel_WhoseAutoFilledPriceIsNew_IsNotDirty()
    {
        using var host = new SettingsHost();
        var model = new ModelSettings(ModelSettings.NewId(), host.Catalog.Current.Connections[0].Id, "claude-sonnet-4-5", "Sonnet", 4096, 50_000, false);
        await host.Catalog.SaveModelAsync(model);
        var vm = NewModels(host, new FakePrices());

        await vm.SelectAsync(vm.Items.Single(i => i.Id == model.Id));

        Assert.Equal("3.00", vm.Pricing!.InputText);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Save_WithoutPriceSection_KeepsTheStoredPricing()
    {
        using var host = new SettingsHost();
        var stored = new ModelPricing(5m, 6m, null, null, false, PriceSource.Confirmed);
        var model = new ModelSettings(ModelSettings.NewId(), host.Catalog.Current.Connections[0].Id, "custom-model", "Custom", 4096, 50_000, false) { Pricing = stored };
        await host.Catalog.SaveModelAsync(model);
        var vm = host.Models;
        await vm.SelectAsync(vm.Items.Single(i => i.Id == model.Id));
        vm.DisplayName = "Renamed";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(stored, host.Catalog.Current.Models.Single(m => m.Id == model.Id).Pricing);
    }

    [Fact]
    public void WithoutPriceSource_TheSectionIsHidden()
    {
        using var host = new SettingsHost();

        Assert.False(host.Models.ShowPricing);
        Assert.Null(host.Models.Pricing);
    }

    [Fact]
    public async Task InvalidPrice_BlocksSave_AndShowsAFieldError()
    {
        using var host = new SettingsHost();
        var vm = NewModels(host, new FakePrices());
        await vm.StartNewAsync(null);
        vm.ModelId = "claude-sonnet-4-5";
        vm.Pricing!.EditCommand.Execute(null);

        vm.Pricing.InputText = "-2";

        Assert.False(vm.CanSave);
        Assert.True(vm.HasFieldErrors);

        vm.Pricing.InputText = "2";
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task ChangingTheConnection_RematchesToLocal()
    {
        using var host = new SettingsHost();
        var ollamaId = "ollama";
        await host.Catalog.SaveConnectionAsync(new ConnectionSettings(ollamaId, "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1"), null);
        var vm = NewModels(host, new FakePrices());
        await vm.StartNewAsync(host.Catalog.Current.Connections[0].Id);
        vm.ModelId = "claude-sonnet-4-5";
        Assert.Equal(PriceSource.Suggested, vm.Pricing!.ToPricing().Source);

        vm.SelectedConnection = vm.Connections.Single(c => c.Id == ollamaId);

        Assert.True(vm.Pricing!.IsLocal);
        Assert.True(vm.Pricing.ToPricing().IsFree);
    }
}
