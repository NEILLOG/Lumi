using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Usage;

namespace Lumi.App.Tests.Headless;

/// <summary>工作台預算提醒列（headless）：沒有提醒時不佔空間、80% 是 Warning 樣式、100% 是 Danger 樣式、查看用量與知道了。</summary>
public class BudgetBannerTests
{
    private sealed class Rig
    {
        public Rig()
        {
            Vm = new BudgetAlertViewModel(() => OpenUsageCalls++);
            Banner = new BudgetBanner { DataContext = Vm };
            Window = new Window { Width = 1040, Height = 600, Content = Banner };
            Window.Show();
            Window.UpdateLayout();
        }

        public int OpenUsageCalls { get; private set; }

        public BudgetAlertViewModel Vm { get; }

        public BudgetBanner Banner { get; }

        public Window Window { get; }

        public Border Card() => Banner.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("budget-banner"));

        public TextBlock Text() => Banner.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("budget-text"));

        public Button Button(string content) =>
            Banner.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == content);
    }

    [AvaloniaFact]
    public void TakesNoSpace_UntilThereIsAnAlert()
    {
        var rig = new Rig();

        Assert.False(rig.Banner.IsVisible);

        rig.Vm.Show(BudgetAlertLevel.Eighty, 8.5m, 10m);
        rig.Window.UpdateLayout();
        Assert.True(rig.Banner.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Eighty_UsesTheWarningStyle_AndTheMoneyText()
    {
        var rig = new Rig();

        rig.Vm.Show(BudgetAlertLevel.Eighty, 8.5m, 10m);
        rig.Window.UpdateLayout();

        var card = rig.Card();
        Assert.Contains("warning", card.Classes);
        Assert.DoesNotContain("danger", card.Classes);
        Assert.NotNull(card.Background);
        Assert.Contains("warning", rig.Text().Classes);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Budget_Alert80Format, "NT$272", "NT$320"), rig.Text().Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Hundred_UsesTheDangerStyle()
    {
        var rig = new Rig();

        rig.Vm.Show(BudgetAlertLevel.Hundred, 11m, 10m);
        rig.Window.UpdateLayout();

        var card = rig.Card();
        Assert.Contains("danger", card.Classes);
        Assert.DoesNotContain("warning", card.Classes);
        Assert.Contains("danger", rig.Text().Classes);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Budget_Alert100Format, "NT$352", "NT$320"), rig.Text().Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ViewUsage_OpensTheUsagePage_AndHidesTheBanner()
    {
        var rig = new Rig();
        rig.Vm.Show(BudgetAlertLevel.Eighty, 8.5m, 10m);
        rig.Window.UpdateLayout();

        rig.Button(Strings.Budget_ViewUsage).Command!.Execute(null);
        rig.Window.UpdateLayout();

        Assert.Equal(1, rig.OpenUsageCalls);
        Assert.False(rig.Banner.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Dismiss_HidesTheBanner_WithoutOpeningUsage()
    {
        var rig = new Rig();
        rig.Vm.Show(BudgetAlertLevel.Hundred, 11m, 10m);
        rig.Window.UpdateLayout();

        rig.Button(Strings.Budget_Dismiss).Command!.Execute(null);
        rig.Window.UpdateLayout();

        Assert.Equal(0, rig.OpenUsageCalls);
        Assert.False(rig.Banner.IsVisible);
        rig.Window.Close();
    }
}
