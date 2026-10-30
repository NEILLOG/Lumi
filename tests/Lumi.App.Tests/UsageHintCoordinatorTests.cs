using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.App.Tests;

/// <summary>使用習慣提示的決定與紀錄（plan/23 第 7.1、7.2 節）：優先順序、靜音、頻率、按鈕寫入的結果。</summary>
public sealed class UsageHintCoordinatorTests
{
    private sealed class Rig
    {
        public InMemoryUsageHintLog Log { get; } = new();
        public FakeModelCatalog Catalog { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public UsageHintCoordinator Coordinator { get; }
        public ComposerHintViewModel Hint { get; } = new();

        public Rig() => Coordinator = new UsageHintCoordinator(Log, Catalog, Time);

        public Task<bool> ShowAsync(string conversationId, params UsageHintMatch[] matches) =>
            Coordinator.TryShowAsync(conversationId, matches, ChatMode.Workbench, Hint);

        public IEnumerable<(string Rule, UsageHintOutcome Outcome)> Outcomes() => Log.Entries.Select(e => (e.Rule, e.Outcome));
    }

    private static UsageHintMatch Match(string rule, params string[] args) => new(rule, args);

    private static UsageHintMatch File(string name, string tokens) => new(UsageHintIds.BigFile, [name, tokens], Key: name);

    [Fact]
    public async Task TheFirstEligibleRuleByPriority_IsShown_AndTheOthersAreSuppressed()
    {
        var rig = new Rig();

        var shown = await rig.ShowAsync("c1", Match(UsageHintIds.Images, "4"), Match(UsageHintIds.LongConversation));

        Assert.True(shown);
        Assert.True(rig.Hint.IsVisible);
        Assert.Equal(Strings.Hint_LongConversation, rig.Hint.Text);
        Assert.Contains((UsageHintIds.LongConversation, UsageHintOutcome.Shown), rig.Outcomes());
        Assert.Contains((UsageHintIds.Images, UsageHintOutcome.Suppressed), rig.Outcomes());
        Assert.Equal(2, rig.Log.Entries.Count);
    }

    [Fact]
    public async Task SpriteMode_NeverShows_AndRecordsEverythingAsSuppressed()
    {
        var rig = new Rig();

        var shown = await rig.Coordinator.TryShowAsync("c1", [Match(UsageHintIds.LongConversation)], ChatMode.Sprite, rig.Hint);

        Assert.False(shown);
        Assert.False(rig.Hint.IsVisible);
        Assert.Equal([(UsageHintIds.LongConversation, UsageHintOutcome.Suppressed)], rig.Outcomes());
    }

    [Fact]
    public async Task MutedRule_IsSuppressed_AndTheNextRuleIsShown()
    {
        var rig = new Rig();
        rig.Catalog.Current = rig.Catalog.Current with { Usage = new UsageSettings(MutedHints: [UsageHintIds.LongConversation]) };

        var shown = await rig.ShowAsync("c1", Match(UsageHintIds.LongConversation), Match(UsageHintIds.Images, "4"));

        Assert.True(shown);
        Assert.Equal(string.Format(Strings.Hint_ImagesFormat, "4"), rig.Hint.Text);
        Assert.Contains((UsageHintIds.LongConversation, UsageHintOutcome.Suppressed), rig.Outcomes());
    }

    [Fact]
    public async Task SameConversation_RuleIsShownOnlyOnce_ButADifferentConversationShowsAgain()
    {
        var rig = new Rig();
        Assert.True(await rig.ShowAsync("c1", Match(UsageHintIds.LongConversation)));
        await rig.Hint.DismissCommand.ExecuteAsync(null);

        Assert.False(await rig.ShowAsync("c1", Match(UsageHintIds.LongConversation)));
        Assert.True(await rig.ShowAsync("c2", Match(UsageHintIds.LongConversation)));
    }

    [Fact]
    public async Task StaleResume_IsNotLimitedToOncePerConversation()
    {
        var rig = new Rig();
        var stale = Match(UsageHintIds.StaleResume, "h:3", "92K");

        Assert.True(await rig.ShowAsync("c1", stale));
        await rig.Hint.DismissCommand.ExecuteAsync(null);
        Assert.True(await rig.ShowAsync("c1", stale));
    }

    [Fact]
    public async Task StaleResume_TextUsesTheElapsedHoursAndTheTokenAbbreviation()
    {
        var rig = new Rig();

        await rig.ShowAsync("c1", Match(UsageHintIds.StaleResume, "h:3", "92K"));

        Assert.Equal(
            string.Format(Strings.Hint_StaleResumeFormat, string.Format(Strings.Hint_ElapsedHoursFormat, "3"), "92K"),
            rig.Hint.Text);
    }

    [Fact]
    public async Task BigFile_SameFileOnlyOnce_ButAnotherFileShows()
    {
        var rig = new Rig();
        Assert.True(await rig.ShowAsync("c1", File("a.html", "37K")));
        await rig.Hint.DismissCommand.ExecuteAsync(null);

        Assert.False(await rig.ShowAsync("c1", File("a.html", "37K")));
        Assert.True(await rig.ShowAsync("c1", File("b.html", "21K")));
    }

    [Fact]
    public async Task McpTools_AtMostOncePerSevenDays_AcrossConversations()
    {
        var rig = new Rig();
        Assert.True(await rig.ShowAsync("c1", Match(UsageHintIds.McpTools, "14K")));
        await rig.Hint.DismissCommand.ExecuteAsync(null);

        rig.Time.Advance(TimeSpan.FromDays(1));
        Assert.False(await rig.ShowAsync("c2", Match(UsageHintIds.McpTools, "14K")));

        rig.Time.Advance(TimeSpan.FromDays(7));
        Assert.True(await rig.ShowAsync("c3", Match(UsageHintIds.McpTools, "14K")));
    }

    [Fact]
    public async Task ActionButton_RecordsActed_AndRunsTheActionKey()
    {
        var rig = new Rig();
        var actions = new List<string>();
        await rig.Coordinator.TryShowAsync("c1", [File("a.html", "37K")], ChatMode.Workbench, rig.Hint, action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        });

        await rig.Hint.PrimaryCommand.ExecuteAsync(null);

        Assert.Equal([UsageHintActions.CompactAndContinue], actions);
        Assert.False(rig.Hint.IsVisible);
        Assert.Contains((UsageHintIds.BigFile, UsageHintOutcome.Acted), rig.Outcomes());
    }

    [Fact]
    public async Task StaleResume_ContinueButton_RecordsActed_WithoutANavigationAction()
    {
        var rig = new Rig();
        var actions = new List<string>();
        await rig.Coordinator.TryShowAsync("c1", [Match(UsageHintIds.StaleResume, "h:3", "92K")], ChatMode.Workbench, rig.Hint, action =>
        {
            actions.Add(action);
            return Task.CompletedTask;
        });

        await rig.Hint.SecondaryCommand.ExecuteAsync(null);

        Assert.Equal([UsageHintActions.Continue], actions);
        Assert.Contains((UsageHintIds.StaleResume, UsageHintOutcome.Acted), rig.Outcomes());
    }

    [Fact]
    public async Task DismissButton_RecordsDismissed()
    {
        var rig = new Rig();
        await rig.ShowAsync("c1", Match(UsageHintIds.LongConversation));

        await rig.Hint.DismissCommand.ExecuteAsync(null);

        Assert.False(rig.Hint.IsVisible);
        Assert.Contains((UsageHintIds.LongConversation, UsageHintOutcome.Dismissed), rig.Outcomes());
    }

    [Fact]
    public async Task MuteButton_RecordsMuted_AndSavesTheRuleToSettings()
    {
        var rig = new Rig();
        await rig.ShowAsync("c1", Match(UsageHintIds.LongConversation));

        await rig.Hint.MuteCommand.ExecuteAsync(null);

        Assert.False(rig.Hint.IsVisible);
        Assert.Contains((UsageHintIds.LongConversation, UsageHintOutcome.Muted), rig.Outcomes());
        Assert.Equal([UsageHintIds.LongConversation], rig.Catalog.Current.Usage.Muted);
        Assert.Single(rig.Catalog.UsageSaves);
    }

    [Fact]
    public async Task ShowingAnotherHint_WhileOneIsVisible_RecordsTheOldOneAsDismissed()
    {
        var rig = new Rig();
        await rig.ShowAsync("c1", Match(UsageHintIds.LongConversation));

        await rig.ShowAsync("c1", Match(UsageHintIds.Images, "4"));

        Assert.Equal(string.Format(Strings.Hint_ImagesFormat, "4"), rig.Hint.Text);
        Assert.Contains((UsageHintIds.LongConversation, UsageHintOutcome.Dismissed), rig.Outcomes());
    }

    [Fact]
    public async Task ModelSwitch_BelowTheThreshold_ReturnsNull_AndWritesNothing()
    {
        var rig = new Rig();

        var prompt = await rig.Coordinator.BeginModelSwitchAsync("c1", UsageHintThresholds.ModelSwitchContextTokens - 1);

        Assert.Null(prompt);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task ModelSwitch_AtTheThreshold_ReturnsThePrompt_AndTheSecondTimeInTheConversationIsNull()
    {
        var rig = new Rig();

        var prompt = await rig.Coordinator.BeginModelSwitchAsync("c1", 92_000);

        Assert.NotNull(prompt);
        Assert.Equal(string.Format(Strings.Hint_ModelSwitchFormat, "92K"), prompt!.Text);
        Assert.Contains((UsageHintIds.ModelSwitch, UsageHintOutcome.Shown), rig.Outcomes());
        Assert.Null(await rig.Coordinator.BeginModelSwitchAsync("c1", 92_000));
        Assert.NotNull(await rig.Coordinator.BeginModelSwitchAsync("c2", 92_000));
    }

    [Fact]
    public async Task ModelSwitch_MutedRule_ReturnsNull()
    {
        var rig = new Rig();
        rig.Catalog.Current = rig.Catalog.Current with { Usage = new UsageSettings(MutedHints: [UsageHintIds.ModelSwitch]) };

        Assert.Null(await rig.Coordinator.BeginModelSwitchAsync("c1", 92_000));
    }
}
