using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M35 35J：工作台的研究本按鈕（顯示條件、啟動流程、回應中停用）、跟著對話切換、研究本變動，以及刪除對話的確認文字。</summary>
public class WorkbenchContextNoteTests
{
    private sealed record Rig(WorkbenchViewModel Vm, FakeChatSession Session, InMemoryConversationStore Store, InMemoryContextNoteStore NoteStore,
        FakeContextNoteService Notes, FakeContextNoteWindowService Window, FakeContextNoteStartPrompt Start, FakeModelCatalog Catalog, FakeConfirmService Confirm,
        string ConversationId);

    private static Rig Create(bool withServices = true, InMemoryConversationStore? store = null, FakeModelCatalog? catalog = null,
        string? conversationId = null, SpriteConversationService? sprite = null)
    {
        store ??= new InMemoryConversationStore();
        catalog ??= new FakeModelCatalog();
        conversationId ??= store.CreateAsync("測試").GetAwaiter().GetResult().Id;
        var session = new FakeChatSession { ConversationId = conversationId };
        var noteStore = new InMemoryContextNoteStore();
        var notes = new FakeContextNoteService();
        var window = new FakeContextNoteWindowService();
        var start = new FakeContextNoteStartPrompt();
        var confirm = new FakeConfirmService();
        var vm = new WorkbenchViewModel(null!, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: session, attachments: new FakeAttachmentStore(),
            deletion: HistoryTestSupport.Deletion(store), confirm: confirm, spriteConversation: sprite,
            contextNotes: withServices ? notes : null, contextNoteStore: noteStore,
            contextNoteWindow: withServices ? window : null, contextNoteStart: withServices ? start : null);
        return new Rig(vm, session, store, noteStore, notes, window, start, catalog, confirm, conversationId);
    }

    private static async Task SeedMessageAsync(InMemoryConversationStore store, string conversationId)
    {
        await store.AppendMessageAsync(conversationId,
            new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("內容")], DateTimeOffset.UtcNow));
    }

    // ---- 顯示條件 ----

    [Fact]
    public async Task Button_IsHidden_WithoutTheServices()
    {
        var rig = Create(withServices: false);
        await rig.Vm.InitializeAsync();

        Assert.False(rig.Vm.ShowContextNoteButton);
    }

    [Fact]
    public async Task Button_IsShown_ForAChatConversation_WithoutANote()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();

        Assert.True(rig.Vm.ShowContextNoteButton);
        Assert.False(rig.Vm.HasContextNote);
        Assert.True(rig.Vm.ContextNoteButtonEnabled);
        Assert.Equal(Strings.ContextNote_ButtonTipStart, rig.Vm.ContextNoteButtonTip);
    }

    [Fact]
    public async Task Button_IsHidden_InTheSpriteConversation()
    {
        var store = new InMemoryConversationStore();
        var sprite = new SpriteConversationService(store);
        var spriteId = (await sprite.GetOrCreateAsync("與小精靈的對話")).Id;
        var rig = Create(store: store, conversationId: spriteId, sprite: sprite);
        await rig.Vm.InitializeAsync();

        Assert.True(rig.Vm.IsSpriteConversation);
        Assert.False(rig.Vm.ShowContextNoteButton);
    }

    [Fact]
    public async Task Button_IsHidden_WhileViewingAScheduledRun()
    {
        var store = new InMemoryConversationStore();
        var runs = new InMemoryScheduledRunStore();
        var service = new FakeScheduledRunService();
        var conversation = await store.CreateAsync("每日摘要", ConversationKind.ScheduledRun);
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("整理今天的新聞")], DateTimeOffset.UtcNow));
        var planned = new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero);
        var run = new ScheduledRun(ScheduledRun.NewId(), "schedule-1", "每日摘要", conversation.Id, planned, planned, planned,
            ScheduledRunTrigger.Scheduled, ScheduledRunStatus.Completed, null, null);
        await runs.AddAsync(run);
        var rig = Create(store: store);
        var vm = new WorkbenchViewModel(null!, store, rig.Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance, session: rig.Session, scheduledRuns: service, runStore: runs,
            runLogger: NullLogger<ScheduledRunViewModel>.Instance, contextNotes: rig.Notes, contextNoteStore: rig.NoteStore,
            contextNoteWindow: rig.Window, contextNoteStart: rig.Start);
        await vm.InitializeAsync();
        Assert.True(vm.ShowContextNoteButton);

        await vm.OpenScheduledRunAsync(run.Id);

        Assert.True(vm.IsViewingRun);
        Assert.False(vm.ShowContextNoteButton);   // 看排程紀錄時：按鈕隱藏（OnPropertyChanged 轉發）
    }

    // ---- 按鈕的動作 ----

    [Fact]
    public async Task WithoutANote_Pressing_OpensTheStartPrompt_AndThenCreatesAndShows()
    {
        var rig = Create();
        await SeedMessageAsync(rig.Store, rig.ConversationId);
        await rig.Vm.InitializeAsync();
        rig.Start.Answer = new ContextNoteStartRequest("比較兩個方案", ContextNoteStartMode.FromConversation);

        await rig.Vm.OpenContextNoteCommand.ExecuteAsync(null);

        var prompt = Assert.Single(rig.Start.Prompts);
        Assert.Equal("", prompt.SuggestedGoal);
        Assert.Equal(1, prompt.MessageCount);
        Assert.Contains("test-model", prompt.ModelText);
        Assert.Contains($"Start:{rig.ConversationId}:比較兩個方案:FromConversation", rig.Notes.Calls);
        Assert.Equal([$"Show:{rig.ConversationId}"], rig.Window.Calls);
        Assert.True(rig.Vm.HasContextNote);
        Assert.Equal(Strings.ContextNote_ButtonTipOpen, rig.Vm.ContextNoteButtonTip);
    }

    [Fact]
    public async Task WithANote_Pressing_OpensTheWindowDirectly()
    {
        var rig = Create();
        await rig.Notes.StartAsync(rig.ConversationId, "既有的目標", ContextNoteStartMode.Blank);
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Vm.HasContextNote);

        await rig.Vm.OpenContextNoteCommand.ExecuteAsync(null);

        Assert.Empty(rig.Start.Prompts);
        Assert.Equal([$"Show:{rig.ConversationId}"], rig.Window.Calls);
    }

    [Fact]
    public async Task WhileResponding_WithoutANote_ButtonIsDisabled_AndSaysToWait()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();

        rig.Session.StartRun();

        Assert.False(rig.Vm.ContextNoteButtonEnabled);
        Assert.False(rig.Vm.OpenContextNoteCommand.CanExecute(null));
        Assert.Equal(Strings.ContextNote_ButtonTipBusy, rig.Vm.ContextNoteButtonTip);

        rig.Session.FinishRun();
        Assert.True(rig.Vm.ContextNoteButtonEnabled);
    }

    [Fact]
    public async Task WhileResponding_WithANote_ButtonStaysEnabled()
    {
        var rig = Create();
        await rig.Notes.StartAsync(rig.ConversationId, "目標", ContextNoteStartMode.Blank);
        await rig.Vm.InitializeAsync();

        rig.Session.StartRun();

        Assert.True(rig.Vm.ContextNoteButtonEnabled);
        Assert.True(rig.Vm.OpenContextNoteCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelledStart_CreatesNothing()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        rig.Start.Answer = null;

        await rig.Vm.OpenContextNoteCommand.ExecuteAsync(null);

        Assert.Single(rig.Start.Prompts);
        Assert.Empty(rig.Notes.Started);
        Assert.Empty(rig.Window.Calls);
        Assert.False(rig.Vm.HasContextNote);
    }

    [Fact]
    public async Task NoModel_PassesNullModelText_ToThePrompt()
    {
        var rig = Create(catalog: new FakeModelCatalog(withModel: false));
        await rig.Vm.InitializeAsync();

        await rig.Vm.OpenContextNoteCommand.ExecuteAsync(null);

        var prompt = Assert.Single(rig.Start.Prompts);
        Assert.Null(prompt.ModelText);
        Assert.Equal(0, prompt.MessageCount);
    }

    [Fact]
    public async Task FailedStart_ShowsANotice_AndKeepsTheConversation()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        rig.Start.Answer = new ContextNoteStartRequest("目標", ContextNoteStartMode.Blank);
        rig.Notes.ThrowOnStart = new InvalidOperationException("boom");

        await rig.Vm.OpenContextNoteCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.HasNotice);
        Assert.Equal(Strings.Workbench_UnexpectedError, rig.Vm.Notice);
        Assert.False(rig.Vm.HasContextNote);
        Assert.Equal(rig.ConversationId, rig.Session.ConversationId);
        Assert.Empty(rig.Window.Calls);
    }

    // ---- 跟著對話、研究本變動 ----

    [Fact]
    public async Task SwitchingConversation_UpdatesTheButtonForTheNewOne()
    {
        var store = new InMemoryConversationStore();
        var first = await store.CreateAsync("有研究本");
        var second = await store.CreateAsync("沒有研究本");
        var rig = Create(store: store, conversationId: first.Id);
        await rig.Notes.StartAsync(first.Id, "目標", ContextNoteStartMode.Blank);
        await rig.Vm.InitializeAsync();
        Assert.True(rig.Vm.HasContextNote);

        rig.Session.SwitchConversation(second.Id);

        Assert.False(rig.Vm.HasContextNote);
        Assert.True(rig.Vm.ContextNoteButtonEnabled);
    }

    [Fact]
    public async Task NoteChanged_ForTheCurrentConversation_RequeriesTheButton()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        Assert.False(rig.Vm.HasContextNote);

        // 背景（書記）建立了研究本：畫面跟著更新
        await rig.Notes.StartAsync(rig.ConversationId, "背景建立", ContextNoteStartMode.Blank);
        rig.Notes.RaiseNoteChanged(new ContextNoteChangedArgs(rig.ConversationId, "n", 1));

        Assert.True(rig.Vm.HasContextNote);
    }

    [Fact]
    public async Task NoteChanged_ForAnotherConversation_IsIgnored()
    {
        var rig = Create();
        await rig.Vm.InitializeAsync();
        await rig.Notes.StartAsync("別的對話", "目標", ContextNoteStartMode.Blank);

        rig.Notes.RaiseNoteChanged(new ContextNoteChangedArgs("別的對話", "n", 1));

        Assert.False(rig.Vm.HasContextNote);
    }

    // ---- 刪除對話的確認文字（工作台「⋯」走 HistoryViewModel） ----

    [Fact]
    public async Task DeleteConfirm_FromTheWorkbench_AddsTheNoteLine_WhenThereIsANote()
    {
        var rig = Create();
        await SeedMessageAsync(rig.Store, rig.ConversationId);
        var note = TestNote(rig.ConversationId);
        await rig.NoteStore.CreateAsync(note, TestRevision(note));
        await rig.Vm.InitializeAsync();

        // 刪除的是目前的對話，之後會切到新的空白對話；假會話不支援建立對話，所以那一步丟例外，確認文字已經在那之前問過了
        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Vm.DeleteConversationCommand.ExecuteAsync(null));

        var asked = Assert.Single(rig.Confirm.Asked);
        Assert.EndsWith("\n\n" + Strings.ContextNote_DeleteWithConversation, asked.Message);
    }

    [Fact]
    public async Task DeleteConfirm_FromTheWorkbench_HasNoNoteLine_WithoutANote()
    {
        var rig = Create();
        await SeedMessageAsync(rig.Store, rig.ConversationId);
        await rig.Vm.InitializeAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => rig.Vm.DeleteConversationCommand.ExecuteAsync(null));

        var asked = Assert.Single(rig.Confirm.Asked);
        Assert.DoesNotContain(Strings.ContextNote_DeleteWithConversation, asked.Message);
    }

    private static ContextNote TestNote(string conversationId) =>
        new(ContextNote.NewId(), conversationId, "目標", true, 1, 0, null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static ContextNoteRevision TestRevision(ContextNote note) =>
        new(ContextNoteRevision.NewId(), note.Id, 1, ContextNoteRevisionKind.StartBlank, note.Goal, "", [], null, 0, DateTimeOffset.UnixEpoch);
}
