using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Chat;

/// <summary>M22 22H：<see cref="IChatSession.CompactAsync"/>（手動整理）。</summary>
public class ChatSessionCompactionTests
{
    private static ChatMessage Msg(ChatRole role, string text) =>
        new(ChatMessage.NewId(), role, [new TextBlock(text)], DateTimeOffset.UtcNow);

    private static async Task<(ChatSession Session, InMemoryConversationStore Store, Conversation Conversation)> Rig(IChatProvider provider, int turns = 3)
    {
        var store = new InMemoryConversationStore();
        var session = new ChatSession(new ConversationService(provider, store, new FakeModelCatalog()), store);
        var conversation = await session.InitializeAsync("新對話");
        for (var i = 0; i < turns; i++)
        {
            await store.AppendMessageAsync(conversation.Id, Msg(ChatRole.User, $"問{i}"));
            await store.AppendMessageAsync(conversation.Id, Msg(ChatRole.Assistant, $"答{i}"));
        }
        return (session, store, conversation);
    }

    private static ChatEvent[] SummaryEvents() =>
        [new UsageEvent(new TokenUsage(50, 5, 0, 0)), new Completed(ScriptedChatProvider.Assistant(new TextBlock("摘要")), StopReason.EndTurn)];

    [Fact]
    public async Task Compact_Succeeds_RaisesEvents_AndLeavesTheSessionIdle()
    {
        var (session, store, conversation) = await Rig(new SequenceChatProvider(SummaryEvents()));
        var seen = new List<ChatEvent>();
        var runEvents = 0;
        session.EventReceived += (_, e) => seen.Add(e);
        session.RunStarted += (_, _) => runEvents++;
        session.RunFinished += (_, _) => runEvents++;

        var result = await session.CompactAsync();

        Assert.True(result.Compacted);
        Assert.Null(result.Failure);
        Assert.False(session.IsResponding);
        Assert.IsType<CompactionStarted>(seen[0]);
        Assert.IsType<CompactionCompleted>(seen[^1]);
        Assert.Equal(0, runEvents);   // 不是一輪回應
        Assert.Equal("摘要", (await store.GetCompactionStateAsync(conversation.Id)).Summary);
    }

    [Fact]
    public async Task Compact_Failure_IsReturnedAndRaised()
    {
        var failure = new ChatFailure(ChatFailureKind.Network, "x", true);
        var (session, store, conversation) = await Rig(new SequenceChatProvider([new Failed(failure)]));
        CompactionFailed? raised = null;
        session.EventReceived += (_, e) => raised ??= e as CompactionFailed;

        var result = await session.CompactAsync();

        Assert.False(result.Compacted);
        Assert.Equal(ChatFailureKind.Network, result.Failure?.Kind);
        Assert.NotNull(raised);
        Assert.Equal(CompactionState.Empty, await store.GetCompactionStateAsync(conversation.Id));
    }

    [Fact]
    public async Task Compact_NothingToCompact_ReturnsNeitherSuccessNorFailure()
    {
        var (session, _, _) = await Rig(new SequenceChatProvider(), turns: 1);

        var result = await session.CompactAsync();

        Assert.False(result.Compacted);
        Assert.Null(result.Failure);
    }

    [Fact]
    public async Task Compact_WhileResponding_Throws()
    {
        var (session, _, _) = await Rig(new ScriptedChatProvider(new TextDelta("…")) { HangAfter = 1 });
        var sending = session.SendAsync("嗨");
        await Task.Delay(50);
        Assert.True(session.IsResponding);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CompactAsync());

        await session.StopAndWaitAsync();
        await sending;
    }

    [Fact]
    public async Task Compact_CanBeStopped_WithoutStoringAnything()
    {
        var provider = new ScriptedChatProvider { HangAfter = 0 };
        var (session, store, conversation) = await Rig(provider);

        var compacting = session.CompactAsync();
        await Task.Delay(50);
        Assert.True(session.IsResponding);
        await session.StopAndWaitAsync();
        var result = await compacting;

        Assert.False(result.Compacted);
        Assert.Null(result.Failure);
        Assert.False(session.IsResponding);
        Assert.Equal(CompactionState.Empty, await store.GetCompactionStateAsync(conversation.Id));
    }
}
