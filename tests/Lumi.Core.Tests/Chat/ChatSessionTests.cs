using System.Runtime.CompilerServices;
using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Chat;

public class ChatSessionTests
{
    private static readonly FakeModelCatalog Catalog = new();

    private sealed class Rig
    {
        public Rig(IChatProvider provider)
        {
            Store = new InMemoryConversationStore();
            Session = new ChatSession(new ConversationService(provider, Store, Catalog), Store);
        }

        public InMemoryConversationStore Store { get; }

        public ChatSession Session { get; }

        public async Task<Conversation> StartAsync()
        {
            var conversation = await Session.InitializeAsync("新對話");
            return conversation;
        }
    }

    private static Rig ReplyRig(string text = "你好") =>
        new(new ScriptedChatProvider(new TextDelta(text), new Completed(ScriptedChatProvider.Assistant(new TextBlock(text)), StopReason.EndTurn)));

    private static Rig HangingRig() => new(new ScriptedChatProvider(new TextDelta("…")) { HangAfter = 1 });

    private sealed class ThrowingProvider : IChatProvider
    {
        public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("壞掉了");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    // ---- 對話 ----

    [Fact]
    public async Task Initialize_CreatesConversation_WhenNoneExists()
    {
        var rig = ReplyRig();
        ConversationChangedArgs? changed = null;
        rig.Session.ConversationChanged += (_, a) => changed = a;

        var conversation = await rig.Session.InitializeAsync("新對話");

        Assert.Equal("新對話", conversation.Title);
        Assert.Equal(conversation.Id, rig.Session.ConversationId);
        Assert.Equal(conversation.Id, changed?.Conversation.Id);
    }

    [Fact]
    public async Task Initialize_LoadsLatestConversation_WhenOneExists()
    {
        var rig = ReplyRig();
        var existing = await rig.Store.CreateAsync("舊的");

        var conversation = await rig.Session.InitializeAsync("新對話");

        Assert.Equal(existing.Id, conversation.Id);
    }

    [Fact]
    public async Task NewConversation_SwitchesAndRaisesChanged()
    {
        var rig = ReplyRig();
        var first = await rig.StartAsync();
        var raised = new List<string>();
        rig.Session.ConversationChanged += (_, a) => raised.Add(a.Conversation.Id);

        var second = await rig.Session.NewConversationAsync("另一則");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, rig.Session.ConversationId);
        Assert.Equal([second.Id], raised);
    }

    [Fact]
    public async Task OpenConversation_SameConversation_DoesNotRaiseChanged()
    {
        var rig = ReplyRig();
        var conversation = await rig.StartAsync();
        var raised = 0;
        rig.Session.ConversationChanged += (_, _) => raised++;

        await rig.Session.OpenConversationAsync(conversation);

        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task OpenConversation_WhileResponding_StopsFirst_ThenSwitches()
    {
        var rig = HangingRig();
        var first = await rig.StartAsync();
        var other = await rig.Store.CreateAsync("另一則");
        var sending = rig.Session.SendAsync("嗨");
        Assert.True(rig.Session.IsResponding);

        await rig.Session.OpenConversationAsync(other);

        var result = await sending;
        Assert.True(result.Cancelled);
        Assert.False(rig.Session.IsResponding);
        Assert.Equal(other.Id, rig.Session.ConversationId);
        Assert.Equal(first.Id, result.ConversationId);   // 被停止的那一輪還是屬於原本的對話
    }

    // ---- 回應 ----

    [Fact]
    public async Task Send_RaisesStartedEventsFinished_InOrder()
    {
        var rig = ReplyRig("答案");
        var conversation = await rig.StartAsync();
        var log = new List<string>();
        rig.Session.RunStarted += (_, a) => log.Add($"start:{a.Kind}:{a.Mode}:{a.UserText}");
        rig.Session.EventReceived += (_, e) => log.Add(e.GetType().Name);
        rig.Session.RunFinished += (_, a) => log.Add($"finish:{a.Result.Failure is null}:{rig.Session.IsResponding}");

        var result = await rig.Session.SendAsync("問題");

        Assert.Equal(["start:Send:Workbench:問題", "TextDelta", "Completed", "finish:True:False"], log);
        Assert.Equal(conversation.Id, result.ConversationId);
        Assert.Null(result.Failure);
        Assert.False(result.Cancelled);
        Assert.False(result.Unexpected);
        Assert.False(rig.Session.IsResponding);
    }

    [Fact]
    public async Task IsResponding_IsTrue_FromStartedUntilFinished()
    {
        var rig = ReplyRig();
        await rig.StartAsync();
        var duringEvents = new List<bool>();
        rig.Session.RunStarted += (_, _) => duringEvents.Add(rig.Session.IsResponding);
        rig.Session.EventReceived += (_, _) => duringEvents.Add(rig.Session.IsResponding);

        await rig.Session.SendAsync("嗨");

        Assert.All(duringEvents, Assert.True);
        Assert.Equal(3, duringEvents.Count);
    }

    [Fact]
    public async Task Send_WhileResponding_IsRejected()
    {
        var rig = HangingRig();
        await rig.StartAsync();
        var first = rig.Session.SendAsync("第一則");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.SendAsync("第二則"));

        await rig.Session.StopAndWaitAsync();
        await first;
    }

    [Fact]
    public async Task Send_WithoutConversation_Throws()
    {
        var rig = ReplyRig();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.SendAsync("嗨"));
    }

    [Fact]
    public async Task Send_UsesTheModeAtSendTime()
    {
        var rig = ReplyRig();
        await rig.StartAsync();
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));
        RunStartedArgs? started = null;
        rig.Session.RunStarted += (_, a) => started = a;

        await rig.Session.SendAsync("嗨");

        Assert.Equal(ChatMode.Sprite, started?.Mode);
    }

    [Fact]
    public async Task Retry_And_Regenerate_ReportTheirKind()
    {
        var rig = ReplyRig();
        var conversation = await rig.StartAsync();
        await rig.Session.SendAsync("嗨");
        var kinds = new List<ChatRunKind>();
        rig.Session.RunStarted += (_, a) =>
        {
            kinds.Add(a.Kind);
            Assert.Null(a.UserText);
        };

        await rig.Session.RegenerateAsync();

        Assert.Equal([ChatRunKind.Regenerate], kinds);
        var messages = await rig.Store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);   // 舊答案被新答案取代
    }

    // ---- 模式 ----

    [Fact]
    public async Task TrySetMode_IsRefusedWhileResponding_AndAllowedAfter()
    {
        var rig = HangingRig();
        await rig.StartAsync();
        var modes = new List<ChatMode>();
        rig.Session.ModeChanged += (_, m) => modes.Add(m);
        var sending = rig.Session.SendAsync("嗨");

        Assert.False(rig.Session.TrySetMode(ChatMode.Sprite));
        Assert.Equal(ChatMode.Workbench, rig.Session.ActiveMode);

        await rig.Session.StopAndWaitAsync();
        await sending;
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));
        Assert.True(rig.Session.TrySetMode(ChatMode.Sprite));   // 沒變就不再通知
        Assert.Equal([ChatMode.Sprite], modes);
    }

    // ---- 停止 ----

    [Fact]
    public async Task Stop_ReportsCancelled_AndKeepsReceivedText()
    {
        var rig = HangingRig();
        var conversation = await rig.StartAsync();
        RunFinishedArgs? finished = null;
        rig.Session.RunFinished += (_, a) => finished = a;
        var sending = rig.Session.SendAsync("嗨");

        rig.Session.Stop();
        var result = await sending;

        Assert.True(result.Cancelled);
        Assert.Null(result.Failure);
        Assert.False(result.Unexpected);
        Assert.Same(result, finished?.Result);
        Assert.False(rig.Session.IsResponding);
        // ConversationService 已經把已收到的文字存成「已停止」
        var messages = await rig.Store.GetMessagesAsync(conversation.Id);
        Assert.Equal(MessageStatus.Stopped, messages[^1].Status);
    }

    [Fact]
    public async Task StopAndWait_WithoutResponse_ReturnsImmediately()
    {
        var rig = ReplyRig();
        await rig.StartAsync();

        await rig.Session.StopAndWaitAsync();

        Assert.False(rig.Session.IsResponding);
    }

    [Fact]
    public async Task StopAndWait_WaitsForWorkDeferredByObservers()
    {
        var rig = HangingRig();
        await rig.StartAsync();
        var release = new TaskCompletionSource();
        rig.Session.RunFinished += (_, a) => a.Defer(release.Task);
        var sending = rig.Session.SendAsync("嗨");

        var stopping = rig.Session.StopAndWaitAsync();
        await Task.Delay(50);
        Assert.False(stopping.IsCompleted);   // 觀察者的收尾還沒做完

        release.SetResult();
        await stopping;
        await sending;
    }

    // ---- 失敗 ----

    [Fact]
    public async Task ProviderFailure_IsReportedInTheResult()
    {
        var failure = new ChatFailure(ChatFailureKind.RateLimited, "太快了", Retryable: true);
        var rig = new Rig(new ScriptedChatProvider(new Failed(failure)));
        await rig.StartAsync();

        var result = await rig.Session.SendAsync("嗨");

        Assert.Same(failure, result.Failure);
        Assert.False(result.Unexpected);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task UnexpectedException_IsReported_AndSessionIsUsableAgain()
    {
        var rig = new Rig(new ThrowingProvider());
        await rig.StartAsync();
        var finishedCount = 0;
        rig.Session.RunFinished += (_, _) => finishedCount++;

        var result = await rig.Session.SendAsync("嗨");

        Assert.True(result.Unexpected);
        Assert.Null(result.Failure);
        Assert.Equal(1, finishedCount);
        Assert.False(rig.Session.IsResponding);
        Assert.False((await rig.Session.SendAsync("再試一次")).Cancelled);
    }

    // ---- 觀察者 ----

    [Fact]
    public async Task EveryObserver_ReceivesTheSameEvents()
    {
        var rig = ReplyRig("嗨");
        await rig.StartAsync();
        var first = new List<ChatEvent>();
        var second = new List<ChatEvent>();
        rig.Session.EventReceived += (_, e) => first.Add(e);
        rig.Session.EventReceived += (_, e) => second.Add(e);

        await rig.Session.SendAsync("嗨");

        Assert.Equal(2, first.Count);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task ObserverException_DoesNotStopTheRunOrOtherObservers()
    {
        var rig = ReplyRig();
        await rig.StartAsync();
        var received = new List<ChatEvent>();
        var finished = 0;
        rig.Session.RunStarted += (_, _) => throw new InvalidOperationException("觀察者壞了");
        rig.Session.EventReceived += (_, _) => throw new InvalidOperationException("觀察者壞了");
        rig.Session.EventReceived += (_, e) => received.Add(e);
        rig.Session.RunFinished += (_, _) => throw new InvalidOperationException("觀察者壞了");
        rig.Session.RunFinished += (_, _) => finished++;

        var result = await rig.Session.SendAsync("嗨");

        Assert.False(result.Unexpected);
        Assert.IsType<Completed>(received[^1]);
        Assert.Equal(1, finished);
        Assert.False(rig.Session.IsResponding);
    }

    [Fact]
    public async Task DeferredWorkThatFails_DoesNotBreakTheRun()
    {
        var rig = ReplyRig();
        await rig.StartAsync();
        rig.Session.RunFinished += (_, a) => a.Defer(Task.FromException(new InvalidOperationException("收尾失敗")));

        var result = await rig.Session.SendAsync("嗨");

        Assert.False(result.Unexpected);
        await rig.Session.StopAndWaitAsync();   // 不會卡住
    }
}
