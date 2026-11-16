using System.Collections.Concurrent;
using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests;

/// <summary>研究本視窗的 ViewModel（M35 35G）：載入、區段與區塊、狀態文字、修改目標、提示、匯出、事件與執行緒。</summary>
public class ContextNoteViewModelTests
{
    private const string Conversation = "conv-1";
    private const string Body = """
        ## 問題拆解
        - 第一點

        ## 已決定
        - 決定甲

        ## 待決問題

        ## 觀點與選項

        ## 否決的選項

        ## 目前的解與待驗證

        ## 旁支
        - 旁支一
        """;

    /// <summary>把事件排進佇列，由測試明確 <see cref="Drain"/>，用來確認「從背景來的事件只在 UI 執行緒更新」。</summary>
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PostCount => _queue.Count;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void Drain()
        {
            while (_queue.TryDequeue(out var item))
                item.Callback(item.State);
        }
    }

    /// <summary>記下每次存檔的目的地與建議檔名，回傳測試指定的路徑（null＝取消）。</summary>
    private sealed class RecordingPicker : IFilePickerService
    {
        public string? Destination { get; set; }

        public List<(string Name, string Extension)> Asked { get; } = [];

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string suggestedName, string extension)
        {
            Asked.Add((suggestedName, extension));
            return Task.FromResult(Destination);
        }
    }

    private sealed class Rig
    {
        public Rig(IUiDispatcher? dispatcher = null)
        {
            Dispatcher = dispatcher ?? new SyncUiDispatcher();
            Editor = new ContextNoteEditorViewModel(Notes, new FakeClipboardImage(), new FakeConfirmService());
            History = new ContextNoteHistoryViewModel(Store, Notes, new FakeConfirmService());
            Vm = new ContextNoteViewModel(Notes, Files, Conversations, Dispatcher, Folders, Picker, Time, Editor, History);
        }

        public FakeContextNoteService Notes { get; } = new();
        public InMemoryContextNoteFiles Files { get; } = new();
        public InMemoryConversationStore Conversations { get; } = new();
        public InMemoryContextNoteStore Store { get; } = new();
        public FakeFolderOpener Folders { get; } = new();
        public RecordingPicker Picker { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public IUiDispatcher Dispatcher { get; }
        public ContextNoteEditorViewModel Editor { get; }
        public ContextNoteHistoryViewModel History { get; }
        public ContextNoteViewModel Vm { get; }
    }

    private static ContextNote NoteOf(string id = "note-1", string goal = "評估本機模型", bool auto = true, string? hint = null) =>
        new(id, Conversation, goal, auto, 1, 0, null, hint, DateTimeOffset.UtcNow, new DateTimeOffset(2026, 11, 16, 9, 5, 0, TimeSpan.Zero));

    private static ContextNoteRevision RevisionOf(ContextNote note, string content, int version = 1,
        ContextNoteRevisionKind kind = ContextNoteRevisionKind.ScribeFirst, IReadOnlyList<string>? protectedBlocks = null, int restored = 0) =>
        new(ContextNoteRevision.NewId(), note.Id, version, kind, note.Goal, content, protectedBlocks ?? [], null, restored, DateTimeOffset.UtcNow);

    private static void Put(Rig rig, string content, ContextNoteRevisionKind kind = ContextNoteRevisionKind.ScribeFirst,
        ContextNoteRevision? previous = null, IReadOnlyList<string>? protectedBlocks = null, ContextNote? note = null, int restored = 0)
    {
        var n = note ?? NoteOf();
        var current = RevisionOf(n, content, previous is null ? 1 : 2, kind, protectedBlocks, restored);
        rig.Notes.Views[Conversation] = new ContextNoteView(n, current, previous);
    }

    private static ContextNoteSectionViewModel Section(ContextNoteViewModel vm, string name) =>
        vm.Sections.Single(s => s.Name == name);

    private static ContextNoteBlockViewModel Block(ContextNoteViewModel vm, string section, string text) =>
        Section(vm, section).Blocks.Single(b => b.Text == text);

    [Fact]
    public async Task Load_SyncsExternalBeforeReading_AndBuildsSevenSectionsInOrder()
    {
        var rig = new Rig();
        Put(rig, Body);

        await rig.Vm.LoadAsync(Conversation);

        Assert.Equal(["GetView:conv-1", "SyncExternal:note-1", "GetView:conv-1"], rig.Notes.Calls);
        Assert.Equal(ContextNoteDocument.SectionNames, rig.Vm.Sections.Select(s => s.Name).ToList());
        Assert.Equal(7, rig.Vm.Sections.Count);
        Assert.True(rig.Vm.HasNote);
        Assert.Equal("note-1", rig.Vm.NoteId);
        Assert.Equal(Conversation, rig.Vm.ConversationId);
        Assert.Equal("評估本機模型", rig.Vm.Goal);
    }

    [Fact]
    public async Task EmptySections_HaveNoBlocks_AndShowTheEmptyHint()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        var open = Section(rig.Vm, ContextNoteDocument.Open);
        Assert.True(open.IsEmpty);
        Assert.True(open.ShowEmpty);
        Assert.False(Section(rig.Vm, ContextNoteDocument.Breakdown).IsEmpty);
    }

    [Fact]
    public async Task SideSection_IsCollapsedByDefault_AndShowsTheCount()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        var side = Section(rig.Vm, ContextNoteDocument.Side);
        Assert.True(side.IsCollapsed);
        Assert.False(side.ShowBlocks);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Side_CountFormat, 1), side.CountText);

        side.ToggleCollapsedCommand.Execute(null);
        Assert.True(side.ShowBlocks);
    }

    [Fact]
    public async Task UpdatedBlocks_OnlyAppearWhenScribeWroteTheLatestVersion()
    {
        var rig = new Rig();
        var old = RevisionOf(NoteOf(), Body.Replace("第一點", "舊的第一點"), 1);
        Put(rig, Body, ContextNoteRevisionKind.ScribeAuto, previous: old);
        await rig.Vm.LoadAsync(Conversation);
        Assert.True(Block(rig.Vm, ContextNoteDocument.Breakdown, "- 第一點").IsUpdated);
        Assert.False(Block(rig.Vm, ContextNoteDocument.Decided, "- 決定甲").IsUpdated);

        var userVersion = RevisionOf(NoteOf(), Body.Replace("第一點", "舊的第一點"), 1);
        Put(rig, Body, ContextNoteRevisionKind.UserEdit, previous: userVersion);
        await rig.Vm.LoadAsync(Conversation);
        Assert.All(rig.Vm.Sections.SelectMany(s => s.Blocks), b => Assert.False(b.IsUpdated));
    }

    [Fact]
    public async Task UserWrittenBlocks_ShowTheProtectedBadge()
    {
        var rig = new Rig();
        Put(rig, Body, protectedBlocks: ["- 決定甲"]);
        await rig.Vm.LoadAsync(Conversation);

        var protectedBlock = Block(rig.Vm, ContextNoteDocument.Decided, "- 決定甲");
        Assert.True(protectedBlock.IsProtected);
        Assert.True(protectedBlock.CanUnprotect);
        Assert.False(protectedBlock.CanConfirm);

        var plain = Block(rig.Vm, ContextNoteDocument.Breakdown, "- 第一點");
        Assert.False(plain.IsProtected);
        Assert.False(plain.CanUnprotect);
    }

    [Fact]
    public async Task Quote_IsSplitOut_AndCollapsedUntilOpened()
    {
        var rig = new Rig();
        const string quoted = "- 用相容端點接（使用者原話：「就這樣接」）";
        Put(rig, Body.Replace("- 第一點", quoted));
        await rig.Vm.LoadAsync(Conversation);

        var block = Block(rig.Vm, ContextNoteDocument.Breakdown, quoted);
        Assert.Equal("- 用相容端點接", block.MainText);
        Assert.Equal("就這樣接", block.Quote);
        Assert.False(block.IsQuoteOpen);
        Assert.Equal(Strings.ContextNote_Quote_Show, block.QuoteToggleText);

        block.IsQuoteOpen = true;
        Assert.Equal(Strings.ContextNote_Quote_Hide, block.QuoteToggleText);
    }

    [Fact]
    public async Task ImageLines_BecomeSeparateParts_WithTheResolvedPath()
    {
        var rig = new Rig();
        var note = NoteOf();
        Put(rig, Body, note: note);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var relative = await rig.Files.SaveImageAsync(note.Id, png, "image/png");
        var text = $"說明文字\n![圖]({relative})";
        Put(rig, Body.Replace("- 第一點", text), note: note);
        await rig.Vm.LoadAsync(Conversation);

        var parts = Block(rig.Vm, ContextNoteDocument.Breakdown, text).Parts;
        Assert.Equal(2, parts.Count);
        Assert.True(parts[0].IsText);
        Assert.Equal("說明文字", parts[0].Markdown);
        Assert.True(parts[1].HasImage);
        Assert.False(parts[1].ImageMissing);
        Assert.Equal(rig.Files.ResolveImagePath(note.Id, relative), parts[1].ImagePath);
    }

    [Fact]
    public async Task MissingImage_IsMarkedMissing()
    {
        var rig = new Rig();
        const string text = "![圖](images/not-there.png)";
        Put(rig, Body.Replace("- 第一點", text));
        await rig.Vm.LoadAsync(Conversation);

        var part = Block(rig.Vm, ContextNoteDocument.Breakdown, text).Parts.Single();
        Assert.True(part.IsImage);
        Assert.False(part.HasImage);
        Assert.True(part.ImageMissing);
    }

    [Theory]
    [InlineData(ScribeState.Running)]
    [InlineData(ScribeState.WaitingForUser)]
    [InlineData(ScribeState.Paused)]
    public async Task StatusText_ForEachActiveState(ScribeState state)
    {
        var rig = new Rig();
        Put(rig, Body);
        rig.Notes.Statuses["note-1"] = new ContextNoteStatus("note-1", state);
        await rig.Vm.LoadAsync(Conversation);

        var expected = state switch
        {
            ScribeState.Running => Strings.ContextNote_Status_Running,
            ScribeState.WaitingForUser => Strings.ContextNote_Status_Waiting,
            _ => Strings.ContextNote_Status_Paused,
        };
        Assert.Equal(expected, rig.Vm.StatusText);
        Assert.False(rig.Vm.ShowRetry);
    }

    [Fact]
    public async Task StatusText_IdleShowsTheVersionAndLocalTime()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(2026, 11, 16, 9, 5, 0, TimeSpan.Zero), rig.Time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Status_UpdatedFormat, 1, local), rig.Vm.StatusText);
        Assert.False(rig.Vm.IsOrganizing);
        Assert.False(rig.Vm.ShowRetry);
    }

    [Theory]
    [InlineData(ScribeState.Failed)]
    [InlineData(ScribeState.Rejected)]
    [InlineData(ScribeState.NoModel)]
    public async Task FailedRejectedNoModel_KeepTheVersionText_AndShowRetry(ScribeState state)
    {
        var rig = new Rig();
        Put(rig, Body);
        rig.Notes.Statuses["note-1"] = new ContextNoteStatus("note-1", state);
        await rig.Vm.LoadAsync(Conversation);

        Assert.Contains("第 1 版", rig.Vm.StatusText, StringComparison.Ordinal);
        Assert.True(rig.Vm.ShowRetry);
        Assert.NotNull(rig.Vm.StatusDetailText);

        await rig.Vm.OrganizeNowCommand.ExecuteAsync(null);
        Assert.Contains("OrganizeNow:note-1", rig.Notes.Calls);
    }

    [Fact]
    public async Task StatusChanged_FromTheCurrentNote_UpdatesTheText()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        rig.Notes.RaiseStatusChanged(new ContextNoteStatus("note-1", ScribeState.Running));

        Assert.Equal(Strings.ContextNote_Status_Running, rig.Vm.StatusText);
        Assert.True(rig.Vm.IsOrganizing);
    }

    [Fact]
    public async Task RestoredCount_ShowsTheRestoredText()
    {
        var rig = new Rig();
        Put(rig, Body, restored: 2);
        await rig.Vm.LoadAsync(Conversation);

        Assert.True(rig.Vm.HasRestored);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Status_RestoredFormat, 2), rig.Vm.RestoredText);
    }

    [Fact]
    public async Task LongNote_ShowsTheReminder()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        Assert.False(rig.Vm.ShowLongNote);

        var long16001 = Body + "\n" + new string('字', ContextNoteLimits.NoteLongChars + 1);
        Put(rig, long16001);
        await rig.Vm.LoadAsync(Conversation);
        Assert.True(rig.Vm.ShowLongNote);
        Assert.True(rig.Vm.ShowHintRow);
    }

    [Fact]
    public async Task EditGoal_RequiresNonBlankAndAtMost200Characters_AndSavesTheTrimmedGoal()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        rig.Vm.EditGoalCommand.Execute(null);
        Assert.True(rig.Vm.IsEditingGoal);
        Assert.Equal("評估本機模型", rig.Vm.GoalDraft);

        rig.Vm.GoalDraft = "   ";
        Assert.False(rig.Vm.SaveGoalCommand.CanExecute(null));
        rig.Vm.GoalDraft = new string('字', ContextNoteLimits.GoalMaxLength + 1);
        Assert.False(rig.Vm.SaveGoalCommand.CanExecute(null));

        rig.Vm.GoalDraft = new string('字', ContextNoteLimits.GoalMaxLength);
        Assert.True(rig.Vm.SaveGoalCommand.CanExecute(null));

        rig.Vm.GoalDraft = "  決定接法  ";
        await rig.Vm.SaveGoalCommand.ExecuteAsync(null);
        Assert.Contains("SetGoal:note-1:決定接法", rig.Notes.Calls);
        Assert.False(rig.Vm.IsEditingGoal);
        Assert.Equal("決定接法", rig.Vm.Goal);
    }

    [Fact]
    public async Task CancelGoal_LeavesTheGoalAlone()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        rig.Vm.EditGoalCommand.Execute(null);
        rig.Vm.GoalDraft = "別的目標";
        rig.Vm.CancelGoalCommand.Execute(null);

        Assert.False(rig.Vm.IsEditingGoal);
        Assert.Equal("評估本機模型", rig.Vm.Goal);
        Assert.DoesNotContain(rig.Notes.Calls, c => c.StartsWith("SetGoal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GoalHint_AcceptEntersGoalEdit_AndDismissClearsIt()
    {
        var rig = new Rig();
        Put(rig, Body, note: NoteOf(hint: "討論已經轉到怎麼接"));
        await rig.Vm.LoadAsync(Conversation);
        Assert.True(rig.Vm.HasGoalHint);

        rig.Vm.AcceptGoalHintCommand.Execute(null);
        Assert.True(rig.Vm.IsEditingGoal);

        rig.Vm.CancelGoalCommand.Execute(null);
        await rig.Vm.DismissGoalHintCommand.ExecuteAsync(null);
        Assert.Contains("DismissGoalHint:note-1", rig.Notes.Calls);
        Assert.False(rig.Vm.HasGoalHint);
        Assert.False(rig.Vm.ShowHintRow);
    }

    [Fact]
    public async Task AutoOrganize_LoadingDoesNotCallTheService_ButUserChangesDo()
    {
        var rig = new Rig();
        Put(rig, Body, note: NoteOf(auto: true));
        await rig.Vm.LoadAsync(Conversation);
        Assert.True(rig.Vm.AutoOrganize);
        Assert.DoesNotContain(rig.Notes.Calls, c => c.StartsWith("SetAutoOrganize", StringComparison.Ordinal));

        rig.Vm.AutoOrganize = false;

        Assert.Contains("SetAutoOrganize:note-1:False", rig.Notes.Calls);
    }

    [Fact]
    public async Task EventsForAnotherNote_AreIgnored()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        var before = rig.Notes.Calls.Count;

        var statusBefore = rig.Vm.StatusText;

        rig.Notes.RaiseNoteChanged(new ContextNoteChangedArgs(Conversation, "other-note", 9));
        rig.Notes.RaiseStatusChanged(new ContextNoteStatus("other-note", ScribeState.Running));

        Assert.Equal(before, rig.Notes.Calls.Count);
        Assert.Equal(statusBefore, rig.Vm.StatusText);
        Assert.False(rig.Vm.IsOrganizing);
    }

    [Fact]
    public async Task EventFromBackgroundThread_OnlyUpdatesOnTheUiContext()
    {
        // 這裡不安裝 SynchronizationContext（會讓 await 的續行排進佇列）：只有 CapturedUiDispatcher 會把工作排進 context
        var context = new QueueContext();
        var rig = new Rig(new CapturedUiDispatcher(context));
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        var callsBefore = rig.Notes.Calls.Count;

        await Task.Run(() =>
        {
            rig.Notes.RaiseNoteChanged(new ContextNoteChangedArgs(Conversation, "note-1", 2));
            rig.Notes.RaiseStatusChanged(new ContextNoteStatus("note-1", ScribeState.Running));
        });

        // 背景執行緒來的事件只排進佇列，還沒有碰到畫面的狀態
        Assert.Equal(2, context.PostCount);
        Assert.Equal(callsBefore, rig.Notes.Calls.Count);
        Assert.False(rig.Vm.IsOrganizing);

        // 回到 UI 執行緒（這裡是測試執行緒）才執行：整理中的狀態與重新讀取都發生在這裡
        context.Drain();

        Assert.True(rig.Vm.IsOrganizing);
        Assert.True(rig.Notes.Calls.Count > callsBefore);
    }

    [Fact]
    public async Task ExportCancelled_DoesNothing()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        rig.Picker.Destination = null;

        await rig.Vm.ExportCommand.ExecuteAsync(null);

        Assert.Single(rig.Picker.Asked);
        Assert.Equal("zip", rig.Picker.Asked[0].Extension);
        Assert.Null(rig.Vm.Notice);
        Assert.False(rig.Vm.HasNotice);
    }

    [Fact]
    public async Task ExportSucceeded_ShowsTheDestination()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        var destination = Path.Combine(Path.GetTempPath(), "lumi-35g-" + Guid.NewGuid().ToString("N") + ".zip");
        rig.Picker.Destination = destination;
        try
        {
            await rig.Vm.ExportCommand.ExecuteAsync(null);

            Assert.True(File.Exists(destination));
            Assert.Equal(string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_ExportDoneFormat, destination), rig.Vm.Notice);
        }
        finally
        {
            File.Delete(destination);
        }
    }

    [Fact]
    public async Task ExportFailed_ShowsTheFailureMessage()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        // 資料夾不存在：寫入會失敗
        rig.Picker.Destination = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing", "x.zip");

        await rig.Vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ContextNote_ExportFailed, rig.Vm.Notice);
        Assert.True(rig.Vm.HasNotice);
    }

    [Fact]
    public async Task OpenFolder_OpensTheNoteFolder()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        await rig.Vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([rig.Files.GetFolder("note-1")], rig.Folders.Opened);
    }

    [Theory]
    [InlineData(ContextNoteDocument.Breakdown, "- 第一點", true, false, false)]
    [InlineData(ContextNoteDocument.Decided, "- 決定甲", false, true, false)]
    [InlineData(ContextNoteDocument.Decided, "- 決定甲", false, false, true)]
    [InlineData(ContextNoteDocument.Breakdown, "- 第一點", true, false, true)]
    public async Task BlockActions_ShowOnlyWhatApplies(string section, string text, bool markDecided, bool confirm, bool protectedNow)
    {
        var rig = new Rig();
        Put(rig, Body, protectedBlocks: protectedNow ? new[] { text } : null);
        await rig.Vm.LoadAsync(Conversation);

        var block = Block(rig.Vm, section, text);
        Assert.Equal(markDecided, block.CanMarkDecided);
        Assert.Equal(confirm, block.CanConfirm);
        Assert.Equal(protectedNow, block.CanUnprotect);
    }

    [Fact]
    public async Task BlockActions_CallTheServiceWithTheBlockText()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);
        var block = Block(rig.Vm, ContextNoteDocument.Breakdown, "- 第一點");

        await block.MarkDecidedCommand.ExecuteAsync(null);
        await block.ConfirmCommand.ExecuteAsync(null);
        await block.UnprotectCommand.ExecuteAsync(null);

        Assert.Contains("MarkDecided:note-1:- 第一點", rig.Notes.Calls);
        Assert.Contains("SetProtected:note-1:- 第一點:True", rig.Notes.Calls);
        Assert.Contains("SetProtected:note-1:- 第一點:False", rig.Notes.Calls);
    }

    [Fact]
    public async Task ShowHistory_SwitchesModeAndLoadsTheHistory()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        await rig.Vm.ShowHistoryCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsHistory);
        Assert.False(rig.Vm.IsPreview);
    }

    [Fact]
    public async Task EnterEdit_WithoutANote_DoesNothing()
    {
        var rig = new Rig();
        await rig.Vm.LoadAsync(Conversation);

        rig.Vm.EnterEditCommand.Execute(null);

        Assert.True(rig.Vm.IsPreview);
        Assert.DoesNotContain(rig.Notes.Calls, c => c.StartsWith("BeginUserEdit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterEdit_WithANote_HandsTheContentToTheEditor()
    {
        var rig = new Rig();
        Put(rig, Body);
        await rig.Vm.LoadAsync(Conversation);

        // 編輯器（35H）還是空殼：Begin 丟 NotImplementedException。接上正式實作後，這裡會改成檢查 BeginUserEdit 與 Mode。
        var ex = Record.Exception(() => rig.Vm.EnterEditCommand.Execute(null));
        Assert.True(ex is null or NotImplementedException);
        if (ex is null)
        {
            Assert.True(rig.Vm.IsEdit);
            Assert.Contains("BeginUserEdit:note-1", rig.Notes.Calls);
        }
    }

    [Fact]
    public void SideBySideTip_FollowsTheAvailability()
    {
        var rig = new Rig();
        rig.Vm.CanSideBySide = true;
        Assert.Equal(Strings.ContextNote_SideBySideTip, rig.Vm.SideBySideTip);
        rig.Vm.CanSideBySide = false;
        Assert.Equal(Strings.ContextNote_SideBySideTooNarrow, rig.Vm.SideBySideTip);
    }

    [Fact]
    public async Task NoteWithoutAnyView_ClearsTheWindow()
    {
        var rig = new Rig();
        await rig.Vm.LoadAsync(Conversation);

        Assert.False(rig.Vm.HasNote);
        Assert.Empty(rig.Vm.Sections);
    }

}
