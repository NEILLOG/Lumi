using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>每月預算提醒的接線（plan/23-usage-cost.md 第 5 節）：回應結束後檢查、工作台提醒列或小精靈說一句、同一門檻只提醒一次。</summary>
public sealed class BudgetAlertCoordinatorTests
{
    private sealed class FakeSpriteNotice : ISpriteNotice
    {
        public List<string> Said { get; } = [];

        public void Say(string text) => Said.Add(text);
    }

    private sealed class Rig
    {
        public Rig(decimal? budgetUsd = 10m, bool withSprite = true)
        {
            Catalog.Current = Catalog.Current with { Usage = new UsageSettings(MonthlyBudgetUsd: budgetUsd) };
            Monitor = new BudgetMonitor(Usage, Meta, Catalog, Time);
            Sprite = withSprite ? new FakeSpriteNotice() : null;
            Coordinator = new BudgetAlertCoordinator(Session, Monitor, Banner, Sprite, new SyncUiDispatcher());
        }

        public FakeModelCatalog Catalog { get; } = new();
        public InMemoryUsageStore Usage { get; } = new();
        public InMemoryConversationStore Meta { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeChatSession Session { get; } = new();
        public BudgetAlertViewModel Banner { get; } = new();
        public BudgetMonitor Monitor { get; }
        public FakeSpriteNotice? Sprite { get; }
        public BudgetAlertCoordinator Coordinator { get; }

        /// <summary>本月多一筆有單價的花費（美元）。</summary>
        public void Spend(decimal usd) =>
            Usage.Records.Add(new UsageRecord(UsageRecord.NewId(), Time.GetUtcNow(), "conv-1", UsagePurpose.Chat, ChatMode.Workbench,
                "ms", "m", "連線", new TokenUsage(100, 10, 0, 0), false, null, usd, 0));

        /// <summary>一輪回應結束（工作台或小精靈發起的都一樣）。</summary>
        public void Finish() => Session.FinishRun();
    }

    [Fact]
    public void WorkbenchMode_Over80_ShowsTheWarningBanner_WithTheMoneyText()
    {
        var rig = new Rig();

        rig.Spend(8.5m);   // 8.5 of 10 USD：NT$272 ／ NT$320
        rig.Finish();

        Assert.True(rig.Banner.IsVisible);
        Assert.False(rig.Banner.IsDanger);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Budget_Alert80Format, "NT$272", "NT$320"), rig.Banner.Text);
        Assert.Empty(rig.Sprite!.Said);
    }

    [Fact]
    public void WorkbenchMode_Over100_ShowsTheDangerBanner()
    {
        var rig = new Rig();

        rig.Spend(11m);
        rig.Finish();

        Assert.True(rig.Banner.IsVisible);
        Assert.True(rig.Banner.IsDanger);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.Budget_Alert100Format, "NT$352", "NT$320"), rig.Banner.Text);
    }

    [Fact]
    public void BelowTheFirstThreshold_ShowsNothing()
    {
        var rig = new Rig();

        rig.Spend(7.9m);
        rig.Finish();

        Assert.False(rig.Banner.IsVisible);
        Assert.Empty(rig.Sprite!.Said);
    }

    [Fact]
    public void SpriteMode_Over80_SaysOneLine_AndDoesNotShowTheBanner()
    {
        var rig = new Rig();
        rig.Session.SetMode(ChatMode.Sprite);

        rig.Spend(8.5m);
        rig.Finish();

        Assert.Equal(new[] { Strings.Sprite_Budget80 }, rig.Sprite!.Said);
        Assert.False(rig.Banner.IsVisible);
    }

    [Fact]
    public void SpriteMode_Over100_SaysTheHundredLine()
    {
        var rig = new Rig();
        rig.Session.SetMode(ChatMode.Sprite);

        rig.Spend(11m);
        rig.Finish();

        Assert.Equal(new[] { Strings.Sprite_Budget100 }, rig.Sprite!.Said);
        Assert.False(rig.Banner.IsVisible);
    }

    [Fact]
    public void SpriteMode_WithoutASpriteNotice_DoesNothing()
    {
        var rig = new Rig(withSprite: false);
        rig.Session.SetMode(ChatMode.Sprite);

        rig.Spend(11m);
        rig.Finish();

        Assert.False(rig.Banner.IsVisible);
    }

    [Fact]
    public void SameThreshold_IsNotShownAgainWithinTheMonth_EvenAfterDismissing()
    {
        var rig = new Rig();
        rig.Spend(8.5m);
        rig.Finish();
        rig.Banner.DismissCommand.Execute(null);

        rig.Spend(0.5m);   // 9 of 10：還是 80% 那一階
        rig.Finish();

        Assert.False(rig.Banner.IsVisible);
    }

    [Fact]
    public void Crossing100_AfterThe80Alert_ShowsTheDangerBanner()
    {
        var rig = new Rig();
        rig.Spend(8.5m);
        rig.Finish();
        Assert.False(rig.Banner.IsDanger);

        rig.Spend(2.5m);   // 11 of 10
        rig.Finish();

        Assert.True(rig.Banner.IsVisible);
        Assert.True(rig.Banner.IsDanger);
    }

    [Fact]
    public void WithoutABudget_NothingIsShown()
    {
        var rig = new Rig(budgetUsd: null);

        rig.Spend(100m);
        rig.Finish();

        Assert.False(rig.Banner.IsVisible);
        Assert.Empty(rig.Sprite!.Said);
    }
}
