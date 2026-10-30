using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Tests.Headless;

/// <summary>M23 23F：模型設定表單的「單價」段落在各種狀態下看得到什麼（自動帶入、候選、找不到、本機、沒有價目表）。</summary>
public class ModelPricingSectionTests
{
    private sealed class FakePrices : IPriceTableSource
    {
        public PriceTable Current { get; } = new(
        [
            new PriceEntry("claude-sonnet-4-5", "anthropic", 3m, 15m, 0.3m, 3.75m, 200_000, 32_000),
            new PriceEntry("azure/gpt-4o", "azure", 2.5m, 10m, null, null, null, null),
            new PriceEntry("openai/gpt-4o", "openai", 2.5m, 10m, null, null, null, null),
        ], new DateOnly(2026, 10, 15));

        public Task<PriceTableUpdateResult> UpdateAsync(CancellationToken ct) => Task.FromResult(new PriceTableUpdateResult(false, Current.SnapshotDate));
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool withPrices = true)
        {
            Host = new SettingsHost(new AvaloniaUiDispatcher());
            Vm = new ModelsViewModel(Host.Catalog, Host.Tester, Host.Status, Host.Confirm, new AvaloniaUiDispatcher(), withPrices ? new FakePrices() : null);
            Window = new Window { Width = 900, Height = 1400, Content = new ModelsPanel { DataContext = Vm } };
            Window.Show();
            Settle();
        }

        public SettingsHost Host { get; }

        public ModelsViewModel Vm { get; }

        public Window Window { get; }

        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public IEnumerable<TextBlock> TextBlocks() => Window.GetVisualDescendants().OfType<TextBlock>();

        public void Dispose()
        {
            Window.Close();
            Host.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AutoFilled_ShowsSummaryWithBadge_AndHidesFields()
    {
        using var rig = new Rig();
        await rig.Vm.StartNewAsync(null);
        rig.Vm.ModelId = "claude-sonnet-4-5";
        rig.Settle();

        Assert.True(rig.Window.FindByName<Control>("PricingSummary").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingFields").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingCandidatesBox").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingFreeText").IsEffectivelyVisible);
        var summary = string.Format(CultureInfo.CurrentCulture, Strings.Pricing_SummaryFormat, "3.00", "15.00");
        Assert.Contains(rig.TextBlocks(), t => t.Text == summary && t.IsEffectivelyVisible);
        Assert.Contains(rig.TextBlocks(), t => t.Text == Strings.Pricing_AutoFilled && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Candidates_ShowTheWarningBox_UntilTheUserChoosesManual()
    {
        using var rig = new Rig();
        await rig.Host.Catalog.SaveConnectionAsync(new ConnectionSettings("gateway", "Gateway", ProviderFormat.OpenAICompatible,
            "https://gateway.corp.example/v1"), null);
        rig.Settle();
        await rig.Vm.StartNewAsync(null);
        rig.Vm.SelectedConnection = rig.Vm.Connections.Single(c => c.Id == "gateway");
        rig.Vm.ModelId = "gpt-4o-prod";
        rig.Settle();

        Assert.True(rig.Window.FindByName<Control>("PricingCandidatesBox").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingSummary").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingFields").IsEffectivelyVisible);

        rig.Window.FindByName<Button>("PricingManualButton").Command!.Execute(null);
        rig.Settle();

        Assert.True(rig.Window.FindByName<Control>("PricingFields").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingCandidatesBox").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task NotFound_ShowsEmptyFieldsWithTheNote()
    {
        using var rig = new Rig();
        await rig.Vm.StartNewAsync(null);
        rig.Vm.ModelId = "qqqqqqqq";
        rig.Settle();

        Assert.True(rig.Window.FindByName<Control>("PricingFields").IsEffectivelyVisible);
        Assert.True(rig.Window.FindByName<Control>("PricingNotFoundText").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingSummary").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task MarkedFree_ShowsOnlyTheFreeLine()
    {
        using var rig = new Rig();
        await rig.Vm.StartNewAsync(null);
        rig.Vm.ModelId = "llama3";
        rig.Settle();
        Assert.False(rig.Window.FindByName<Control>("PricingFreeText").IsEffectivelyVisible);

        rig.Window.FindByName<CheckBox>("PricingFreeCheckBox").IsChecked = true;
        rig.Settle();

        Assert.True(rig.Window.FindByName<Control>("PricingFreeText").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingSummary").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingFields").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingCandidatesBox").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Editing_ShowsTheFieldsAndTheUpdateButton()
    {
        using var rig = new Rig();
        await rig.Vm.StartNewAsync(null);
        rig.Vm.ModelId = "claude-sonnet-4-5";
        rig.Settle();

        rig.Vm.Pricing!.EditCommand.Execute(null);
        rig.Settle();

        Assert.True(rig.Window.FindByName<Control>("PricingFields").IsEffectivelyVisible);
        Assert.False(rig.Window.FindByName<Control>("PricingSummary").IsEffectivelyVisible);
        Assert.True(rig.Window.FindByName<Control>("PricingUpdateButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void WithoutAPriceTable_TheWholeSectionIsHidden()
    {
        using var rig = new Rig(withPrices: false);

        Assert.False(rig.Window.FindByName<Control>("PricingSection").IsEffectivelyVisible);
    }
}
