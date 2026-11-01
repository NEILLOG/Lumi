using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tarot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M25 25C：工作台事件序列下的回覆附件歸屬、停止、失敗補句，以及 <c>ToolCallStarted</c> 不另開段落。</summary>
public class WorkbenchReplyAttachmentTests
{
    private const string SpreadJson = """
        {"spread_id":"three_card_timeline","spread_name":"三張牌","allow_reversed":true,"positions":[
          {"index":1,"position_name":"過去","layout":{"x":-1,"y":0,"rotation":0,"z":0},
           "card":{"id":"major_00","name_zh":"愚者","name_en":"The Fool","element":null,"orientation":"upright"}}]}
        """;

    private sealed record Rig(WorkbenchViewModel Vm, FakeChatSession Session, InMemoryConversationStore Store, string ConversationId);

    private static Rig CreateRig()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.CreateAsync("測試").GetAwaiter().GetResult();
        var session = new FakeChatSession { ConversationId = conversation.Id };
        var vm = new WorkbenchViewModel(null!, store, new FakeModelCatalog(), new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: session);
        return new Rig(vm, session, store, conversation.Id);
    }

    private static ToolCallStarted Started(string callId, string toolName) =>
        new(callId, toolName, toolName, ToolPermission.ReadOnly, null, "{}");

    private static ToolCallCompleted TarotDone(string callId) =>
        new(callId, new ToolResultBlock(callId, SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired));

    private static Completed Text(string text, StopReason reason = StopReason.EndTurn) =>
        new(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow), reason);

    private static ChatRunResult Stopped(string conversationId) => new(conversationId, null, false, true);

    /// <summary>
    /// 停止之後存檔的樣子（<c>ConversationService</c> 的結果）：第一個工具已完成，第二個呼叫被停止、摘要「已停止」。
    /// 停止時工作台會從存檔重讀，所以這些要先放進存檔裡。
    /// </summary>
    private static async Task SeedStoppedRunAsync(Rig rig)
    {
        var now = DateTimeOffset.UtcNow;
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("請抽牌")], now));
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ToolUseBlock("t1", TarotToolNames.Draw, "{}"), new ToolUseBlock("t2", "echo", "{}")], now));
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [
                new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired),
                new ToolResultBlock("t2", "使用者已停止。", true, "回聲", null, "已停止", 0, ToolApproval.NotRequired),
            ], now));
    }

    [Fact]
    public void TarotThenText_AttachesToTheAnswerBubble_AndNoHostIsAdded()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        rig.Session.Emit(TarotDone("t1"));
        rig.Session.Emit(new TextDelta("解讀"));
        rig.Session.Emit(Text("解讀"));
        rig.Session.FinishRun(new ChatRunResult(rig.ConversationId, null, false, false));

        Assert.Equal(3, vm.Messages.Count);   // 使用者（送出的 x）、工具段落、答案
        var answer = vm.Messages[2];
        Assert.Equal("解讀", answer.Text);
        Assert.False(answer.IsAttachmentHost);
        var attachment = Assert.Single(answer.ReplyAttachments);
        Assert.IsType<TarotAttachmentViewModel>(attachment);
        Assert.False(vm.Messages[1].HasReplyAttachments);
        Assert.True(answer.ShowActions);
        Assert.True(answer.CanCopy);
    }

    [Fact]
    public void ToolCompletedBeforeAnyText_ShowsTheDotsBubbleWithTheAttachment()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        rig.Session.Emit(TarotDone("t1"));

        var waiting = vm.Messages[^1];
        Assert.True(waiting.IsStreaming);
        Assert.True(waiting.HasReplyAttachments);
        Assert.True(waiting.ShowTypingDots);
        Assert.False(waiting.ShowTypingIndicator);
    }

    private static Completed Empty() =>
        new(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [], DateTimeOffset.UtcNow), StopReason.EndTurn);

    [Fact]
    public void EmptyAnswerAfterTarot_LeavesOnlyTheAttachmentHost()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        rig.Session.Emit(TarotDone("t1"));
        rig.Session.Emit(Empty());
        rig.Session.FinishRun(new ChatRunResult(rig.ConversationId, null, false, false));

        var host = vm.Messages[^1];
        Assert.True(host.IsAttachmentHost);
        Assert.IsType<ToolRunViewModel>(vm.Messages[^2]);
        Assert.True(host.ShowActions);
    }

    [Fact]
    public void EmptyAnswerWithoutTools_IsKeptAsBefore()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Empty());
        rig.Session.FinishRun(new ChatRunResult(rig.ConversationId, null, false, false));

        var last = vm.Messages[^1];
        Assert.True(last.IsAssistant);
        Assert.False(last.IsAttachmentHost);
        Assert.False(last.HasText);
    }

    [Fact]
    public async Task EmptyAnswerAfterTarot_ReloadsTheSameWay()
    {
        var rig = CreateRig();
        var vm = rig.Vm;
        var now = DateTimeOffset.UtcNow;
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("請抽牌")], now));
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ToolUseBlock("t1", TarotToolNames.Draw, "{}")], now));
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired)], now));
        await rig.Store.AppendMessageAsync(rig.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [], now));

        var other = await rig.Store.CreateAsync("別的對話");
        await vm.OpenConversationAsync(other);
        var saved = await rig.Store.GetConversationAsync(rig.ConversationId);
        await vm.OpenConversationAsync(saved!);

        Assert.Equal(3, vm.Messages.Count);   // 使用者、工具段落、只放附件的訊息（空白回答不顯示）
        Assert.True(vm.Messages[^1].IsAttachmentHost);
        Assert.IsType<TarotAttachmentViewModel>(Assert.Single(vm.Messages[^1].ReplyAttachments));
    }

    [Fact]
    public void ToolCallStartedWhileTheHostIsLast_JoinsTheSameRun()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        rig.Session.Emit(TarotDone("t1"));
        rig.Session.Emit(Started("t2", "echo"));   // 沒有文字就接著下一個工具

        Assert.Single(vm.Messages.OfType<ToolRunViewModel>());
        var run = vm.Messages.OfType<ToolRunViewModel>().Single();
        Assert.Equal(new[] { "t1", "t2" }, run.Cards.Select(c => c.CallId).ToList());
        Assert.True(vm.Messages[^1].IsAttachmentHost);
        Assert.Same(run, vm.Messages[^2]);
    }

    [Fact]
    public async Task Stopped_WithoutText_EndsWithAStoppedHost()
    {
        var rig = CreateRig();
        var vm = rig.Vm;
        await SeedStoppedRunAsync(rig);

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        rig.Session.Emit(TarotDone("t1"));
        rig.Session.Emit(Started("t2", "echo"));
        rig.Session.Emit(new ToolCallCompleted("t2", new ToolResultBlock("t2", "使用者已停止。", true, "回聲", null, "已停止", 0, ToolApproval.NotRequired)));
        rig.Session.FinishRun(Stopped(rig.ConversationId));

        var host = vm.Messages[^1];
        Assert.True(host.IsAttachmentHost);
        Assert.True(host.IsStopped);
        Assert.True(host.ShowStoppedNote);
        Assert.True(host.ShowActions);       // 最後一則：重新產生
        Assert.False(host.CanCopy);          // 沒有文字可複製
        Assert.False(host.ShowBubble);
        Assert.True(host.IsLastAssistant);
        Assert.Single(host.ReplyAttachments);
    }

    [Fact]
    public async Task StoppedAfterReload_StillShowsTheStoppedNote()
    {
        var rig = CreateRig();
        var vm = rig.Vm;
        await SeedStoppedRunAsync(rig);

        // 重讀（例如重開對話）：先切到另一則，再切回來，只靠存檔裡的「已停止」卡片還原
        var other = await rig.Store.CreateAsync("別的對話");
        await vm.OpenConversationAsync(other);
        var saved = await rig.Store.GetConversationAsync(rig.ConversationId);
        await vm.OpenConversationAsync(saved!);

        var host = vm.Messages[^1];
        Assert.True(host.IsAttachmentHost);
        Assert.True(host.ShowStoppedNote);
    }

    [Fact]
    public void Failure_WithTarotAttachment_AppendsTheKeptNote()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        rig.Session.Emit(TarotDone("t1"));
        rig.Session.FinishRun(new ChatRunResult(rig.ConversationId, new ChatFailure(ChatFailureKind.RateLimited, "服務忙碌，請稍後再試。", true), false, false));

        Assert.True(vm.Messages[^1].IsAttachmentHost);
        Assert.Equal("服務忙碌，請稍後再試。 " + Strings.Attachment_KeptTarot, vm.ErrorMessage);
        Assert.EndsWith("抽到的牌已保留。", vm.ErrorMessage);
    }

    [Fact]
    public void Failure_WithoutAttachments_LeavesTheMessageAlone()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.FinishRun(new ChatRunResult(rig.ConversationId, new ChatFailure(ChatFailureKind.RateLimited, "服務忙碌，請稍後再試。", true), false, false));

        Assert.Equal("服務忙碌，請稍後再試。", vm.ErrorMessage);
    }

    [Fact]
    public void CardAttachmentChange_RefreshesTheMessages()
    {
        var rig = CreateRig();
        var vm = rig.Vm;

        rig.Session.StartRun();
        rig.Session.Emit(Started("t1", TarotToolNames.Draw));
        var run = vm.Messages.OfType<ToolRunViewModel>().Single();
        Assert.False(vm.Messages[^1].IsAttachmentHost);

        // 卡片完成時附件換掉：工作台即時重算（不必等下一個事件）
        run.Cards[0].Complete(new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired));

        Assert.True(vm.Messages[^1].HasReplyAttachments);
    }
}
