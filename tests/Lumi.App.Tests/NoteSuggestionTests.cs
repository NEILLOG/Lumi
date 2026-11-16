using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Tools;
using Lumi.Tools.ContextNotes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M35 35J：建議卡片的三種狀態、按鈕（開研究本、不用、打開研究本）、回應中停用，以及不產生卡片的情況。</summary>
public class NoteSuggestionTests
{
    private sealed record Rig(WorkbenchViewModel Vm, FakeChatSession Session, InMemoryConversationStore Store, InMemoryContextNoteStore NoteStore,
        FakeContextNoteService Notes, FakeContextNoteWindowService Window, FakeContextNoteStartPrompt Start, string ConversationId);

    private static Rig Create(InMemoryConversationStore? store = null, InMemoryContextNoteStore? noteStore = null, string? conversationId = null,
        bool withServices = true)
    {
        store ??= new InMemoryConversationStore();
        conversationId ??= store.CreateAsync("測試").GetAwaiter().GetResult().Id;
        noteStore ??= new InMemoryContextNoteStore();
        var session = new FakeChatSession { ConversationId = conversationId };
        var notes = new FakeContextNoteService();
        var window = new FakeContextNoteWindowService();
        var start = new FakeContextNoteStartPrompt();
        var vm = new WorkbenchViewModel(null!, store, new FakeModelCatalog(), new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: session, attachments: new FakeAttachmentStore(),
            contextNotes: withServices ? notes : null, contextNoteStore: noteStore,
            contextNoteWindow: withServices ? window : null, contextNoteStart: withServices ? start : null);
        return new Rig(vm, session, store, noteStore, notes, window, start, conversationId);
    }

    /// <summary>一輪回應裡，模型呼叫了 <c>suggest_context_note</c> 並成功（回應還在進行中）。</summary>
    private static void EmitSuggestion(Rig rig, string goal = "比較兩個方案", string callId = "s1")
    {
        rig.Session.StartRun();
        rig.Session.Emit(new ToolCallStarted(callId, SuggestContextNoteTool.ToolName, "建議開研究本", ToolPermission.ReadOnly, null,
            $"{{\"goal\":\"{goal}\"}}"));
        rig.Session.Emit(new ToolCallCompleted(callId,
            new ToolResultBlock(callId, "已經在畫面上詢問使用者。", false, "建議開研究本", null, goal, 0, ToolApproval.NotRequired)));
    }

    private static NoteSuggestionAttachmentViewModel Card(WorkbenchViewModel vm) =>
        Assert.Single(vm.Messages.SelectMany(m => m.ReplyAttachments).OfType<NoteSuggestionAttachmentViewModel>());

    [Fact]
    public async Task AfterTheToolSucceeds_TheCardIsPending_WithTheGoal()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();

        EmitSuggestion(rig, "比較兩個方案");
        rig.Session.FinishRun();

        var card = Card(rig.Vm);
        Assert.True(card.IsPending);
        Assert.False(card.IsCreated);
        Assert.False(card.IsDismissed);
        Assert.Equal("比較兩個方案", card.Goal);
        Assert.Equal(NoteSuggestionState.Pending, card.State);
    }

    [Fact]
    public async Task Create_OpensTheStartPrompt_WithTheSuggestedGoal_AndThenCreatesTheNote()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        EmitSuggestion(rig, "比較兩個方案");
        rig.Session.FinishRun();
        var card = Card(rig.Vm);
        rig.Start.Answer = new ContextNoteStartRequest("比較兩個方案（定稿）", ContextNoteStartMode.FromConversation);

        await card.CreateCommand.ExecuteAsync(null);

        var prompt = Assert.Single(rig.Start.Prompts);
        Assert.Equal("比較兩個方案", prompt.SuggestedGoal);
        Assert.Contains($"Start:{rig.ConversationId}:比較兩個方案（定稿）:FromConversation", rig.Notes.Calls);
        Assert.Equal([$"Show:{rig.ConversationId}"], rig.Window.Calls);
        Assert.True(card.IsCreated);
        Assert.Equal(NoteSuggestionState.Created, card.State);
    }

    [Fact]
    public async Task Create_Cancelled_LeavesTheCardPending()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        EmitSuggestion(rig);
        rig.Session.FinishRun();
        var card = Card(rig.Vm);
        rig.Start.Answer = null;

        await card.CreateCommand.ExecuteAsync(null);

        Assert.Single(rig.Start.Prompts);
        Assert.Empty(rig.Notes.Started);
        Assert.True(card.IsPending);
    }

    [Fact]
    public async Task Dismiss_TurnsTheCardDismissed_AndReopeningTheConversationStillShowsDismissed()
    {
        var store = new InMemoryConversationStore();
        var noteStore = new InMemoryContextNoteStore();
        var rig = Create(store: store, noteStore: noteStore);
        await rig.Vm.InitializeAsync();
        EmitSuggestion(rig);
        rig.Session.FinishRun();
        var card = Card(rig.Vm);

        await card.DismissCommand.ExecuteAsync(null);

        Assert.True(card.IsDismissed);
        Assert.False(card.IsPending);
        Assert.False(card.DismissCommand.CanExecute(null));

        // 重開對話：新的 ViewModel 讀同一個 store，狀態從資料庫算
        var reopened = Create(store: store, noteStore: noteStore, conversationId: rig.ConversationId);
        await reopened.Vm.InitializeAsync();
        Assert.Equal(NoteSuggestionState.Dismissed, reopened.Vm.SuggestionState);
    }

    [Fact]
    public async Task WhenTheNoteAlreadyExists_TheCardIsCreated_AndOpenShowsTheWindow()
    {
        var rig = Create();
        await rig.Notes.StartAsync(rig.ConversationId, "既有的目標", ContextNoteStartMode.Blank);
        await rig.Vm.InitializeAsync();
        EmitSuggestion(rig);
        rig.Session.FinishRun();
        var card = Card(rig.Vm);

        Assert.True(card.IsCreated);

        await card.OpenCommand.ExecuteAsync(null);

        Assert.Equal([$"Show:{rig.ConversationId}"], rig.Window.Calls);
    }

    [Fact]
    public async Task WhileResponding_TheCreateButtonIsDisabled_AndEnablesAfterTheRun()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        EmitSuggestion(rig);
        var card = Card(rig.Vm);

        Assert.False(card.CanCreate);
        Assert.False(card.CreateCommand.CanExecute(null));
        Assert.True(card.IsPending);   // 回應中仍然可以不用、可以看到

        rig.Session.FinishRun();

        Assert.True(card.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutTheNoteServices_NoCardIsProduced()
    {
        var rig = Create(withServices: false);
        await rig.Vm.InitializeAsync();

        EmitSuggestion(rig);
        rig.Session.FinishRun();

        Assert.Empty(rig.Vm.Messages.SelectMany(m => m.ReplyAttachments).OfType<NoteSuggestionAttachmentViewModel>());
    }

    [Fact]
    public async Task AFailedToolResult_NoCardIsProduced()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        rig.Session.StartRun();
        rig.Session.Emit(new ToolCallStarted("s1", SuggestContextNoteTool.ToolName, "建議開研究本", ToolPermission.ReadOnly, null, "{\"goal\":\"目標\"}"));
        rig.Session.Emit(new ToolCallCompleted("s1",
            new ToolResultBlock("s1", "這則對話已經建議過，不要再建議。", true, "建議開研究本", null, "錯誤", 0, ToolApproval.NotRequired)));
        rig.Session.FinishRun();

        Assert.Empty(rig.Vm.Messages.SelectMany(m => m.ReplyAttachments).OfType<NoteSuggestionAttachmentViewModel>());
    }

    [Fact]
    public void ToolCard_WithoutAHost_MakesNoSuggestionCard()
    {
        // 排程紀錄的卡片不經過工作台，沒有宿主：就算成功也不產生建議卡片
        var card = new ToolCardViewModel("s1", SuggestContextNoteTool.ToolName, "建議開研究本", ToolPermission.ReadOnly, null,
            "{\"goal\":\"目標\"}", new ToolApprovalService(), new FakeClipboardService(), new FakeTimeProvider());

        card.Complete(new ToolResultBlock("s1", "已經在畫面上詢問使用者。", false, "建議開研究本", null, "目標", 0, ToolApproval.NotRequired));

        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public void ToolCard_WithAHost_ProducesOneSuggestionCard_OnlyForTheSuggestTool()
    {
        var rig = Create();
        var host = rig.Vm;
        var suggest = new ToolCardViewModel("s1", SuggestContextNoteTool.ToolName, "建議開研究本", ToolPermission.ReadOnly, null,
            "{\"goal\":\"目標\"}", new ToolApprovalService(), new FakeClipboardService(), new FakeTimeProvider()) { NoteSuggestions = host };
        var other = new ToolCardViewModel("o1", "echo", "回聲", ToolPermission.ReadOnly, null,
            "{\"goal\":\"目標\"}", new ToolApprovalService(), new FakeClipboardService(), new FakeTimeProvider()) { NoteSuggestions = host };

        suggest.Complete(new ToolResultBlock("s1", "好", false, "建議開研究本", null, null, 0, ToolApproval.NotRequired));
        other.Complete(new ToolResultBlock("o1", "好", false, "回聲", null, null, 0, ToolApproval.NotRequired));

        var card = Assert.Single(suggest.ReplyAttachments);
        Assert.IsType<NoteSuggestionAttachmentViewModel>(card);
        Assert.Empty(other.ReplyAttachments);
    }
}
