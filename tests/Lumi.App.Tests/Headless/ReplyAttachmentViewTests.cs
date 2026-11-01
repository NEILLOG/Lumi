using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tarot;
using Microsoft.Extensions.Logging.Abstractions;
using CoreText = Lumi.Core.Chat.TextBlock;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M25 25C：回覆附件在助理訊息裡的位置（名稱與時間 → 附件 → 氣泡）、三圓點、停止說明。</summary>
public class ReplyAttachmentViewTests
{
    private const string SpreadJson = """
        {"spread_id":"three_card_timeline","spread_name":"三張牌","allow_reversed":true,"positions":[
          {"index":1,"position_name":"過去","layout":{"x":-1,"y":0,"rotation":0,"z":0},
           "card":{"id":"major_00","name_zh":"愚者","name_en":"The Fool","element":null,"orientation":"upright"}}]}
        """;

    private sealed record Shown(WorkbenchWindow Window, WorkbenchViewModel Vm, ConversationView View, FakeChatSession Session, InMemoryConversationStore Store, string ConversationId);

    private static Shown Show()
    {
        var store = new InMemoryConversationStore();
        var conversation = store.CreateAsync("測試").GetAwaiter().GetResult();
        var session = new FakeChatSession { ConversationId = conversation.Id };
        var vm = new WorkbenchViewModel(null!, store, new FakeModelCatalog(), new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: session);
        var window = new WorkbenchWindow(vm) { Width = 1200 };   // Wide，和其他對話畫面測試一致
        window.Show();
        window.UpdateLayout();
        return new Shown(window, vm, window.FindByName<ConversationView>("Conversation"), session, store, conversation.Id);
    }

    private static Point Position(Visual visual, ConversationView view) => visual.TranslatePoint(default, view)!.Value;

    [AvaloniaFact]
    public void Attachments_SitBetweenTheNameRowAndTheBubble()
    {
        var shown = Show();
        var session = shown.Session;
        session.StartRun();
        session.Emit(new ToolCallStarted("t1", TarotToolNames.Draw, "塔羅抽牌", ToolPermission.ReadOnly, null, "{}"));
        session.Emit(new ToolCallCompleted("t1", new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired)));
        session.Emit(new TextDelta("解讀"));
        session.Emit(new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new CoreText("解讀")], DateTimeOffset.UtcNow), StopReason.EndTurn));
        session.FinishRun(new ChatRunResult(shown.ConversationId, null, false, false));
        shown.Window.UpdateLayout();
        var view = shown.View;

        var name = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("sender") && t.Text == Strings.Workbench_AssistantName && t.IsEffectivelyVisible);
        var attachments = view.GetVisualDescendants().OfType<ReplyAttachmentsView>().Single(a => a.IsEffectivelyVisible);
        var bubble = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AssistantBubble" && b.IsEffectivelyVisible);

        Assert.True(Position(name, view).Y < Position(attachments, view).Y);
        Assert.True(Position(attachments, view).Y < Position(bubble, view).Y);
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void WhileWaitingWithAttachments_ThreeDotsReplaceTheBubbleText()
    {
        var shown = Show();
        var session = shown.Session;
        session.StartRun();
        session.Emit(new ToolCallStarted("t1", TarotToolNames.Draw, "塔羅抽牌", ToolPermission.ReadOnly, null, "{}"));
        session.Emit(new ToolCallCompleted("t1", new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired)));
        shown.Window.UpdateLayout();
        var view = shown.View;

        var bubble = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AssistantBubble" && b.IsEffectivelyVisible);
        Assert.Equal(3, bubble.GetVisualDescendants().OfType<Ellipse>().Count(e => e.IsEffectivelyVisible));
        Assert.DoesNotContain(bubble.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "…" && t.IsEffectivelyVisible);
        Assert.Equal(40, bubble.Bounds.Height);
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task Stopped_WithoutText_ShowsTheStoppedNoteUnderTheAttachments_AndNoBubble()
    {
        var shown = Show();
        var now = DateTimeOffset.UtcNow;
        await shown.Store.AppendMessageAsync(shown.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new CoreText("請抽牌")], now));
        await shown.Store.AppendMessageAsync(shown.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new ToolUseBlock("t1", TarotToolNames.Draw, "{}"), new ToolUseBlock("t2", "echo", "{}")], now));
        await shown.Store.AppendMessageAsync(shown.ConversationId, new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [
                new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired),
                new ToolResultBlock("t2", "使用者已停止。", true, "回聲", null, "已停止", 0, ToolApproval.NotRequired),
            ], now));

        var session = shown.Session;
        session.StartRun();
        session.Emit(new ToolCallStarted("t1", TarotToolNames.Draw, "塔羅抽牌", ToolPermission.ReadOnly, null, "{}"));
        session.Emit(new ToolCallCompleted("t1", new ToolResultBlock("t1", SpreadJson, false, "塔羅抽牌", null, null, 0, ToolApproval.NotRequired)));
        session.Emit(new ToolCallStarted("t2", "echo", "回聲", ToolPermission.ReadOnly, null, "{}"));
        session.Emit(new ToolCallCompleted("t2", new ToolResultBlock("t2", "使用者已停止。", true, "回聲", null, "已停止", 0, ToolApproval.NotRequired)));
        session.FinishRun(new ChatRunResult(shown.ConversationId, null, false, true));
        shown.Window.UpdateLayout();
        var view = shown.View;

        var note = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == Strings.Workbench_Stopped && t.IsEffectivelyVisible);
        var attachments = view.GetVisualDescendants().OfType<ReplyAttachmentsView>().Single(a => a.IsEffectivelyVisible);
        Assert.True(Position(attachments, view).Y < Position(note, view).Y);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Border>(), b => b.Name == "AssistantBubble" && b.IsEffectivelyVisible);
        shown.Window.Close();
    }
}
