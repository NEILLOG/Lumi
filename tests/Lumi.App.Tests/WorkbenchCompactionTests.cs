using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M22 22I：工作台的整理畫面（整理中提示、摘要分隔線、整理失敗提示、手動整理「整理並繼續」）。</summary>
public sealed class WorkbenchCompactionTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; }
        public FakeModelCatalog Catalog { get; } = new();
        public WorkbenchViewModel Vm { get; }

        public Rig(IChatProvider provider, InMemoryConversationStore? store = null)
        {
            Store = store ?? new InMemoryConversationStore();
            var service = new ConversationService(provider, Store, Catalog);
            Vm = new WorkbenchViewModel(service, Store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
        }
    }

    /// <summary>有 <paramref name="turns"/> 輪的舊對話（使用者與助理交替）。</summary>
    private static async Task<InMemoryConversationStore> SeedAsync(int turns)
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("舊對話");
        for (var i = 0; i < turns; i++)
        {
            await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock($"問{i}")], DateTimeOffset.UtcNow));
            await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock($"答{i}")], DateTimeOffset.UtcNow));
        }
        return store;
    }

    private static ChatEvent[] Summary(string text) => SequenceChatProvider.Answer(text);

    private static List<CompactionDividerViewModel> Dividers(WorkbenchViewModel vm) => vm.Messages.OfType<CompactionDividerViewModel>().ToList();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task ManualCompaction_ShowsOneDivider_AfterTheLastSummarizedMessage()
    {
        var store = await SeedAsync(turns: 3);
        var rig = new Rig(new SequenceChatProvider(Summary("摘要內容")), store);
        await rig.Vm.InitializeAsync();

        await rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);

        var divider = Assert.Single(Dividers(rig.Vm));
        Assert.Equal("摘要內容", divider.Summary);
        var index = rig.Vm.Messages.IndexOf(divider);
        Assert.Equal("答0", rig.Vm.Messages[index - 1].Text);   // SummaryUptoSeq = 第 2 則：摘要涵蓋到它為止
        Assert.False(rig.Vm.IsCompacting);
        Assert.True(rig.Vm.CanCompactAndContinue);
    }

    [Fact]
    public async Task ManualCompaction_ShrinksTheContextUsage_BeforeTheNextResponse()
    {
        var store = await SeedAsync(turns: 3);
        var conversation = (await store.ListAsync(10)).Single();
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問3")], DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("答3")],
            DateTimeOffset.UtcNow, Usage: new TokenUsage(90_000, 1_000, 0, 0)));
        var rig = new Rig(new SequenceChatProvider(Summary("摘要")), store);
        await rig.Vm.InitializeAsync();
        Assert.Equal(91_000, rig.Vm.ContextUsed);

        await rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);

        Assert.InRange(rig.Vm.ContextUsed, 1, 1_000);   // 整理後的估算，不是舊的 91K
    }

    [Fact]
    public async Task TheDivider_ToggleTextFollowsTheExpansion()
    {
        var store = await SeedAsync(turns: 3);
        var rig = new Rig(new SequenceChatProvider(Summary("摘要")), store);
        await rig.Vm.InitializeAsync();
        await rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);
        var divider = Assert.Single(Dividers(rig.Vm));

        Assert.False(divider.IsSummaryOpen);
        Assert.Equal(Strings.Compact_ShowSummary, divider.ToggleText);

        divider.IsSummaryOpen = true;

        Assert.Equal(Strings.Compact_HideSummary, divider.ToggleText);
    }

    [Fact]
    public async Task ReopeningTheConversation_ShowsTheDividerAgain()
    {
        var store = await SeedAsync(turns: 3);
        var first = new Rig(new SequenceChatProvider(Summary("摘要")), store);
        await first.Vm.InitializeAsync();
        await first.Vm.CompactAndContinueCommand.ExecuteAsync(null);

        var reopened = new Rig(new SequenceChatProvider(), store);
        await reopened.Vm.InitializeAsync();

        var divider = Assert.Single(Dividers(reopened.Vm));
        Assert.Equal("摘要", divider.Summary);
    }

    [Fact]
    public async Task NothingToCompact_ShowsNoDividerAndNoFailure()
    {
        var store = await SeedAsync(turns: 1);
        var rig = new Rig(new SequenceChatProvider(), store);
        await rig.Vm.InitializeAsync();

        await rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);

        Assert.Empty(Dividers(rig.Vm));
        Assert.False(rig.Vm.HasCompactionFailure);
    }

    [Fact]
    public async Task WhileCompacting_TheProgressShows_SendIsBlocked_AndStopHidesIt()
    {
        var store = await SeedAsync(turns: 3);
        // 摘要呼叫一開始就掛住（沒有任何事件）：整理中的狀態會停在這裡
        var rig = new Rig(new SequenceChatProvider(new ChatEvent[] { }) { HangOnCall = 1 }, store);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "還要問";

        var compacting = rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);
        await WaitUntil(() => rig.Vm.IsCompacting);

        Assert.False(rig.Vm.CanCompactAndContinue);
        Assert.False(rig.Vm.SendCommand.CanExecute(null));
        Assert.False(rig.Vm.RetryCommand.CanExecute(null));

        await rig.Vm.StopAndWaitAsync();
        await compacting;

        Assert.False(rig.Vm.IsCompacting);   // 被停止：沒有結束事件，這裡照樣清掉
        Assert.True(rig.Vm.SendCommand.CanExecute(null));
        Assert.Empty(Dividers(rig.Vm));
    }

    [Fact]
    public async Task AFailedSummary_ShowsTheNotice_DoesNotBlockSending_AndCanBeDismissed()
    {
        var store = await SeedAsync(turns: 3);
        var failure = new ChatFailure(ChatFailureKind.Network, "連線失敗", true);
        var rig = new Rig(new SequenceChatProvider(new ChatEvent[] { new Failed(failure) }), store);
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "還要問";

        await rig.Vm.CompactAndContinueCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.HasCompactionFailure);
        Assert.False(rig.Vm.IsCompacting);
        Assert.Empty(Dividers(rig.Vm));
        Assert.True(rig.Vm.SendCommand.CanExecute(null));   // 不擋繼續送出

        rig.Vm.DismissCompactionFailureCommand.Execute(null);

        Assert.False(rig.Vm.HasCompactionFailure);
    }

    [Fact]
    public void ConversationTimeline_WithOnlyToolResultsCompacted_ShowsNoDivider()
    {
        var history = new List<ChatMessage>
        {
            new(ChatMessage.NewId(), ChatRole.User, [new TextBlock("問")], DateTimeOffset.UtcNow) { Seq = 1 },
            new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("答")], DateTimeOffset.UtcNow) { Seq = 2 },
        };

        var items = ConversationTimeline.Build(history, _ => throw new InvalidOperationException("沒有工具"), new CompactionState(ToolResultsUptoSeq: 1, SummaryUptoSeq: null, Summary: null));

        Assert.DoesNotContain(items, item => item is CompactionDividerViewModel);
        Assert.Equal(2, items.Count);
    }
}
