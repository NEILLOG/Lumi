using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Infrastructure.ContextNotes;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests.ContextNotes;

public sealed class SqliteContextNoteStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-note-store-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewConversationStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    private SqliteContextNoteStore NewStore() => new(NewConversationStore());

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static DateTimeOffset T(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset Base = T("2026-11-16T09:00+08:00");

    private static async Task<string> NewConversationAsync(SqliteConversationStore conversations, string title = "研究")
    {
        var conversation = await conversations.CreateAsync(title, ConversationKind.Chat);
        return conversation.Id;
    }

    private static ContextNote NewNote(string noteId, string conversationId, string goal = "目標") =>
        new(noteId, conversationId, goal, AutoOrganize: true, CurrentVersion: 1, ScribedUptoSeq: 0, ScribedUptoMessageId: null,
            GoalHint: null, CreatedAt: Base, UpdatedAt: Base);

    private static ContextNoteRevision Revision(
        string noteId,
        int version,
        ContextNoteRevisionKind kind = ContextNoteRevisionKind.StartConversation,
        string goal = "目標",
        string content = "## 問題拆解\n內容",
        IReadOnlyList<string>? protectedBlocks = null,
        string? trigger = null,
        DateTimeOffset? at = null) =>
        new(ContextNote.NewId(), noteId, version, kind, goal, content, protectedBlocks ?? [], trigger, 0, at ?? Base.AddMinutes(version));

    /// <summary>建立一則對話與它的研究本（第一版），回傳研究本 id。</summary>
    private static async Task<(string NoteId, string ConversationId)> CreateNoteAsync(SqliteConversationStore conversations, SqliteContextNoteStore store)
    {
        var conversationId = await NewConversationAsync(conversations);
        var note = NewNote(ContextNote.NewId(), conversationId);
        await store.CreateAsync(note, Revision(note.Id, 1));
        return (note.Id, conversationId);
    }

    [Fact]
    public async Task Create_ThenGetByConversationAndById_RoundTripsEveryField()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var conversationId = await NewConversationAsync(conversations);
        var noteId = ContextNote.NewId();
        var note = new ContextNote(noteId, conversationId, "做一份報告", AutoOrganize: false, CurrentVersion: 1,
            ScribedUptoSeq: 4, ScribedUptoMessageId: "msg-4", GoalHint: "要不要改目標？", CreatedAt: Base, UpdatedAt: Base.AddMinutes(1));
        var first = Revision(noteId, 1, ContextNoteRevisionKind.ScribeFirst, goal: "做一份報告", content: "內容一",
            protectedBlocks: ["受保護的一段"], trigger: "剛剛的問題", at: Base);

        await store.CreateAsync(note, first);

        var byConversation = await store.GetByConversationAsync(conversationId);
        var byId = await store.GetAsync(noteId);
        Assert.NotNull(byConversation);
        Assert.Equal(byId, byConversation);
        Assert.Equal(noteId, byId!.Id);
        Assert.Equal(conversationId, byId.ConversationId);
        Assert.Equal("做一份報告", byId.Goal);
        Assert.False(byId.AutoOrganize);
        Assert.Equal(1, byId.CurrentVersion);
        Assert.Equal(4, byId.ScribedUptoSeq);
        Assert.Equal("msg-4", byId.ScribedUptoMessageId);
        Assert.Equal("要不要改目標？", byId.GoalHint);
        Assert.Equal(Base, byId.CreatedAt);
        Assert.Equal(Base.AddMinutes(1), byId.UpdatedAt);

        var revision = await store.GetRevisionAsync(noteId, 1);
        Assert.NotNull(revision);
        Assert.Equal(first.Id, revision!.Id);
        Assert.Equal(ContextNoteRevisionKind.ScribeFirst, revision.Kind);
        Assert.Equal("內容一", revision.Content);
        Assert.Equal(new[] { "受保護的一段" }, revision.ProtectedBlocks);
        Assert.Equal("剛剛的問題", revision.TriggerExcerpt);
        Assert.Equal(Base, revision.CreatedAt);
    }

    [Fact]
    public async Task Create_NullGoalHintAndNoTrigger_ReadBackAsNull()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);

        var note = await store.GetAsync(noteId);
        var revision = await store.GetRevisionAsync(noteId, 1);
        Assert.Null(note!.GoalHint);
        Assert.Null(note.ScribedUptoMessageId);
        Assert.Null(revision!.TriggerExcerpt);
        Assert.Empty(revision.ProtectedBlocks);
    }

    [Fact]
    public async Task Create_SecondNoteForSameConversation_ThrowsInvalidOperation()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var conversationId = await NewConversationAsync(conversations);
        var firstNote = NewNote(ContextNote.NewId(), conversationId);
        await store.CreateAsync(firstNote, Revision(firstNote.Id, 1));

        var second = NewNote(ContextNote.NewId(), conversationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(second, Revision(second.Id, 1)));
    }

    [Fact]
    public async Task Create_ForMissingConversation_Throws()
    {
        var store = NewStore();
        var note = NewNote(ContextNote.NewId(), "no-such-conversation");

        // 外鍵失敗不是「已經有研究本」，原樣丟出
        await Assert.ThrowsAsync<SqliteException>(() => store.CreateAsync(note, Revision(note.Id, 1)));
        Assert.Null(await store.GetAsync(note.Id));
    }

    [Fact]
    public async Task AddRevision_UpdatesCurrentVersionGoalAndUpdatedAt()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);
        var later = Base.AddHours(2);

        await store.AddRevisionAsync(Revision(noteId, 2, ContextNoteRevisionKind.UserEdit, goal: "新目標", content: "改過", at: later));

        var note = await store.GetAsync(noteId);
        Assert.Equal(2, note!.CurrentVersion);
        Assert.Equal("新目標", note.Goal);
        Assert.Equal(later, note.UpdatedAt);
        Assert.Equal(Base, note.CreatedAt);
        Assert.Equal("改過", (await store.GetRevisionAsync(noteId, 2))!.Content);
        Assert.Equal("內容", (await store.GetRevisionAsync(noteId, 1))!.Content.Split('\n')[1]);
    }

    [Fact]
    public async Task AddRevision_WithProgress_UpdatesProgress()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);

        await store.AddRevisionAsync(Revision(noteId, 2, ContextNoteRevisionKind.ScribeAuto), new ContextNoteProgress(9, "msg-9"));

        var note = await store.GetAsync(noteId);
        Assert.Equal(9, note!.ScribedUptoSeq);
        Assert.Equal("msg-9", note.ScribedUptoMessageId);
    }

    [Fact]
    public async Task AddRevision_WithoutProgressOrHintChange_KeepsThem()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);
        await store.SetProgressAsync(noteId, new ContextNoteProgress(3, "msg-3"));
        await store.SetGoalHintAsync(noteId, "提示");

        await store.AddRevisionAsync(Revision(noteId, 2, ContextNoteRevisionKind.UserEdit));

        var note = await store.GetAsync(noteId);
        Assert.Equal(3, note!.ScribedUptoSeq);
        Assert.Equal("msg-3", note.ScribedUptoMessageId);
        Assert.Equal("提示", note.GoalHint);
    }

    [Fact]
    public async Task AddRevision_SetGoalHintFalse_LeavesGoalHintAlone()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);
        await store.SetGoalHintAsync(noteId, "原本的提示");

        await store.AddRevisionAsync(Revision(noteId, 2), setGoalHint: false, goalHint: "不該寫入");

        Assert.Equal("原本的提示", (await store.GetAsync(noteId))!.GoalHint);
    }

    [Fact]
    public async Task AddRevision_SetGoalHintTrue_WritesGoalHint()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);
        await store.SetGoalHintAsync(noteId, "舊的提示");

        await store.AddRevisionAsync(Revision(noteId, 2), setGoalHint: true, goalHint: "新的提示");
        Assert.Equal("新的提示", (await store.GetAsync(noteId))!.GoalHint);

        await store.AddRevisionAsync(Revision(noteId, 3), setGoalHint: true, goalHint: null);
        Assert.Null((await store.GetAsync(noteId))!.GoalHint);
    }

    [Fact]
    public async Task SetProgress_SetAutoOrganize_SetGoalHint_Update()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);

        await store.SetProgressAsync(noteId, new ContextNoteProgress(7, "msg-7"));
        await store.SetAutoOrganizeAsync(noteId, false);
        await store.SetGoalHintAsync(noteId, "提示文字");

        var note = await store.GetAsync(noteId);
        Assert.Equal(7, note!.ScribedUptoSeq);
        Assert.Equal("msg-7", note.ScribedUptoMessageId);
        Assert.False(note.AutoOrganize);
        Assert.Equal("提示文字", note.GoalHint);

        await store.SetAutoOrganizeAsync(noteId, true);
        await store.SetGoalHintAsync(noteId, null);
        note = await store.GetAsync(noteId);
        Assert.True(note!.AutoOrganize);
        Assert.Null(note.GoalHint);
    }

    [Fact]
    public async Task SetProgress_OnMissingNote_Throws()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetProgressAsync("missing", new ContextNoteProgress(1, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetAutoOrganizeAsync("missing", true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetGoalHintAsync("missing", "x"));
    }

    [Fact]
    public async Task ListRevisions_NewestFirst_WithInfoOnly()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);
        await store.AddRevisionAsync(Revision(noteId, 2, ContextNoteRevisionKind.ScribeAuto, trigger: "第二次的問題", at: Base.AddHours(1)));
        await store.AddRevisionAsync(Revision(noteId, 3, ContextNoteRevisionKind.Revert, trigger: "2", at: Base.AddHours(2)));

        var list = await store.ListRevisionsAsync(noteId);

        ContextNoteRevisionInfo[] expected =
        [
                new ContextNoteRevisionInfo(3, ContextNoteRevisionKind.Revert, "2", Base.AddHours(2)),
                new ContextNoteRevisionInfo(2, ContextNoteRevisionKind.ScribeAuto, "第二次的問題", Base.AddHours(1)),
                new ContextNoteRevisionInfo(1, ContextNoteRevisionKind.StartConversation, null, Base.AddMinutes(1)),
        ];
        Assert.Equal(expected, list);
    }

    [Fact]
    public async Task ListRevisions_NoteWithoutRevisions_IsEmpty()
    {
        var store = NewStore();
        Assert.Empty(await store.ListRevisionsAsync("missing"));
    }

    [Fact]
    public async Task AddRevision_OverMaxRevisions_PrunesOldestExceptFirst()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);

        // 第 1 版加上 200 版：共 201 版，要剪掉 1 版，而且剪掉的必須是第 2 版
        for (var version = 2; version <= ContextNoteLimits.MaxRevisions + 1; version++)
            await store.AddRevisionAsync(Revision(noteId, version, ContextNoteRevisionKind.ScribeAuto, content: $"第 {version} 版"));

        var list = await store.ListRevisionsAsync(noteId);
        Assert.Equal(ContextNoteLimits.MaxRevisions, list.Count);
        Assert.Equal(ContextNoteLimits.MaxRevisions + 1, list[0].Version);
        Assert.Equal(1, list[^1].Version);
        Assert.Null(await store.GetRevisionAsync(noteId, 2));
        Assert.NotNull(await store.GetRevisionAsync(noteId, 1));
        Assert.NotNull(await store.GetRevisionAsync(noteId, 3));
    }

    [Fact]
    public async Task ProtectedBlocks_WithChineseAndNewlines_RoundTrip()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var conversationId = await NewConversationAsync(conversations);
        var noteId = ContextNote.NewId();
        var blocks = new[] { "- [x] 決定使用 SQLite\n第二行：「引號」與 \"雙引號\"", "反斜線 \\ 與 Tab\t結尾" };
        await store.CreateAsync(NewNote(noteId, conversationId), Revision(noteId, 1, protectedBlocks: blocks));

        var revision = await store.GetRevisionAsync(noteId, 1);
        Assert.Equal(blocks, revision!.ProtectedBlocks);
    }

    [Theory]
    [InlineData(ContextNoteRevisionKind.StartConversation, "start_conversation")]
    [InlineData(ContextNoteRevisionKind.StartBlank, "start_blank")]
    [InlineData(ContextNoteRevisionKind.ScribeFirst, "scribe_first")]
    [InlineData(ContextNoteRevisionKind.ScribeAuto, "scribe_auto")]
    [InlineData(ContextNoteRevisionKind.ScribeManual, "scribe_manual")]
    [InlineData(ContextNoteRevisionKind.UserEdit, "user_edit")]
    [InlineData(ContextNoteRevisionKind.UserAction, "user_action")]
    [InlineData(ContextNoteRevisionKind.UserGoal, "user_goal")]
    [InlineData(ContextNoteRevisionKind.External, "external")]
    [InlineData(ContextNoteRevisionKind.Revert, "revert")]
    public async Task Kind_RoundTripsThroughTheDatabase(ContextNoteRevisionKind kind, string stored)
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, _) = await CreateNoteAsync(conversations, store);
        await store.AddRevisionAsync(Revision(noteId, 2, kind));

        Assert.Equal(kind, (await store.GetRevisionAsync(noteId, 2))!.Kind);
        Assert.Equal(kind, (await store.ListRevisionsAsync(noteId))[0].Kind);

        // 欄位存的是小寫底線字串
        await using var connection = await conversations.OpenAsync(default);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM context_note_revisions WHERE note_id = $note AND version = 2;";
        command.Parameters.AddWithValue("$note", noteId);
        Assert.Equal(stored, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DeleteConversation_RemovesNoteRevisionsAndSuggestion()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var (noteId, conversationId) = await CreateNoteAsync(conversations, store);
        await store.AddRevisionAsync(Revision(noteId, 2));
        await store.TryMarkSuggestedAsync(conversationId, Base);

        Assert.True(await conversations.DeleteAsync(conversationId));

        Assert.Null(await store.GetByConversationAsync(conversationId));
        Assert.Null(await store.GetAsync(noteId));
        Assert.Null(await store.GetRevisionAsync(noteId, 1));
        Assert.Empty(await store.ListRevisionsAsync(noteId));
        Assert.Null(await store.GetSuggestionAsync(conversationId));
    }

    [Fact]
    public async Task TryMarkSuggested_SecondCallReturnsFalse_AndKeepsFirstTime()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var conversationId = await NewConversationAsync(conversations);

        Assert.True(await store.TryMarkSuggestedAsync(conversationId, Base));
        Assert.False(await store.TryMarkSuggestedAsync(conversationId, Base.AddHours(1)));

        var suggestion = await store.GetSuggestionAsync(conversationId);
        Assert.Equal(new ContextNoteSuggestion(conversationId, Base, null), suggestion);
    }

    [Fact]
    public async Task DismissSuggestion_WithoutRecord_CreatesOne()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var conversationId = await NewConversationAsync(conversations);

        await store.DismissSuggestionAsync(conversationId, Base.AddHours(1));

        var suggestion = await store.GetSuggestionAsync(conversationId);
        Assert.NotNull(suggestion);
        Assert.Equal(Base.AddHours(1), suggestion!.SuggestedAt);
        Assert.Equal(Base.AddHours(1), suggestion.DismissedAt);
    }

    [Fact]
    public async Task DismissSuggestion_WithRecord_OnlySetsDismissedAt()
    {
        var conversations = NewConversationStore();
        var store = NewStore();
        var conversationId = await NewConversationAsync(conversations);
        await store.TryMarkSuggestedAsync(conversationId, Base);

        await store.DismissSuggestionAsync(conversationId, Base.AddHours(2));

        var suggestion = await store.GetSuggestionAsync(conversationId);
        Assert.Equal(Base, suggestion!.SuggestedAt);
        Assert.Equal(Base.AddHours(2), suggestion.DismissedAt);
    }
}
