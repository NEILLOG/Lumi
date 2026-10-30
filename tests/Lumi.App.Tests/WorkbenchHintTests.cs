using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>工作台的使用習慣提示接線（plan/23 第 7 節）：重新產生第 3 次、送出時收起、輸入框聚焦的第 3 條、沒有協調者時完全不出現。</summary>
public sealed class WorkbenchHintTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; }
        public FakeModelCatalog Catalog { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public InMemoryUsageHintLog Log { get; } = new();
        public WorkbenchViewModel Vm { get; }

        public Rig(SequenceChatProvider provider, InMemoryConversationStore? store = null, bool withHints = true)
        {
            Store = store ?? new InMemoryConversationStore();
            var service = new ConversationService(provider, Store, Catalog);
            var hints = withHints ? new UsageHintCoordinator(Log, Catalog, Time) : null;
            Vm = new WorkbenchViewModel(service, Store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), Time,
                NullLogger<WorkbenchViewModel>.Instance, hintCoordinator: hints);
        }

        public async Task SendAsync(string text = "問")
        {
            Vm.InputText = text;
            await Vm.SendCommand.ExecuteAsync(null);
        }

        public string RegenerateText(int count) => string.Format(Strings.Hint_RegenerateFormat, count.ToString());
    }

    private static SequenceChatProvider Answers(int count) =>
        new(Enumerable.Range(1, count).Select(i => SequenceChatProvider.Answer($"第 {i} 個回答")).ToArray());

    [Fact]
    public async Task ThirdRegenerate_ShowsTheRegenerateHint_AndTheSecondDoesNot()
    {
        var rig = new Rig(Answers(4));
        await rig.Vm.InitializeAsync();
        await rig.SendAsync();

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);
        await rig.Vm.RegenerateCommand.ExecuteAsync(null);
        Assert.False(rig.Vm.Hint.IsVisible);

        await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.Hint.IsVisible);
        Assert.Equal(rig.RegenerateText(UsageHintThresholds.RegenerateCount), rig.Vm.Hint.Text);
        Assert.Null(rig.Vm.Hint.PrimaryText);
        Assert.Null(rig.Vm.Hint.SecondaryText);
        Assert.Contains(rig.Log.Entries, e => e.Rule == UsageHintIds.Regenerate && e.Outcome == UsageHintOutcome.Shown);
    }

    [Fact]
    public async Task SendingANewMessage_CollapsesTheVisibleHint_AndRecordsDismissed()
    {
        var rig = new Rig(Answers(5));
        await rig.Vm.InitializeAsync();
        await rig.SendAsync();
        for (var i = 0; i < UsageHintThresholds.RegenerateCount; i++)
            await rig.Vm.RegenerateCommand.ExecuteAsync(null);
        Assert.True(rig.Vm.Hint.IsVisible);

        await rig.SendAsync("再問一次");

        Assert.False(rig.Vm.Hint.IsVisible);
        Assert.Contains(rig.Log.Entries, e => e.Rule == UsageHintIds.Regenerate && e.Outcome == UsageHintOutcome.Dismissed);
    }

    [Fact]
    public async Task WithoutACoordinator_NoHintEverAppears()
    {
        var rig = new Rig(Answers(4), withHints: false);
        await rig.Vm.InitializeAsync();
        await rig.SendAsync();

        for (var i = 0; i < UsageHintThresholds.RegenerateCount; i++)
            await rig.Vm.RegenerateCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.Hint.IsVisible);
        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task FocusingTheInputAfterAnHourAway_ShowsTheResumeHint_OnlyOncePerOpen()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("舊對話");
        var idleSince = new FakeTimeProvider().GetUtcNow() - TimeSpan.FromHours(2);
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new TextBlock("好")], idleSince, ModelId: "m", Usage: new TokenUsage(60_000, 0, 0, 0)));
        var rig = new Rig(Answers(1), store);
        await rig.Vm.InitializeAsync();

        await rig.Vm.OnComposerFocusedAsync();

        Assert.True(rig.Vm.Hint.IsVisible);
        Assert.Equal(
            string.Format(Strings.Hint_StaleResumeFormat, string.Format(Strings.Hint_ElapsedHoursFormat, "2"), "60K"),
            rig.Vm.Hint.Text);
        Assert.True(rig.Vm.Hint.HasPrimary);
        Assert.True(rig.Vm.Hint.HasSecondary);

        await rig.Vm.Hint.DismissCommand.ExecuteAsync(null);
        await rig.Vm.OnComposerFocusedAsync();
        Assert.False(rig.Vm.Hint.IsVisible);
    }

    [Fact]
    public async Task FocusingTheInput_WithoutACoordinator_DoesNothing()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("舊對話");
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new TextBlock("好")], new FakeTimeProvider().GetUtcNow() - TimeSpan.FromHours(2), ModelId: "m",
            Usage: new TokenUsage(60_000, 0, 0, 0)));
        var rig = new Rig(Answers(1), store, withHints: false);
        await rig.Vm.InitializeAsync();

        await rig.Vm.OnComposerFocusedAsync();

        Assert.False(rig.Vm.Hint.IsVisible);
    }
}
