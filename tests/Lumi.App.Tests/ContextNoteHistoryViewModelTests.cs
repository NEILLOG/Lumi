using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests;

/// <summary>M35 35I：研究本版本歷史的 ViewModel（清單、差異、退回、回到研究本）。</summary>
public class ContextNoteHistoryViewModelTests
{
    private const string NoteId = "note-1";
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public InMemoryContextNoteStore Store { get; } = new();
        public FakeContextNoteService Notes { get; } = new();
        public FakeConfirmService Confirm { get; } = new();
        public ContextNoteHistoryViewModel Vm { get; }
        public int BackCount { get; private set; }

        public Rig()
        {
            Vm = new ContextNoteHistoryViewModel(Store, Notes, Confirm);
            Vm.BackRequested += (_, _) => BackCount++;
        }

        /// <summary>依序建立版本（版本號要由小到大；第一個用來建立研究本）。</summary>
        public async Task SeedAsync(params ContextNoteRevision[] revisions)
        {
            await Store.CreateAsync(Note(revisions[0].Version), revisions[0]);
            foreach (var revision in revisions.Skip(1))
                await Store.AddRevisionAsync(revision);
        }
    }

    private static ContextNote Note(int current) => new(NoteId, "conv-1", "目標", true, current, 0, null, null, Start, Start);

    private static ContextNoteRevision Rev(
        int version, ContextNoteRevisionKind kind, string content, string goal = "目標",
        string? excerpt = null, IReadOnlyList<string>? protectedBlocks = null) =>
        new(ContextNoteRevision.NewId(), NoteId, version, kind, goal, content, protectedBlocks ?? [], excerpt, 0, Start.AddMinutes(version));

    /// <summary>組出 7 個區段裡有給的那幾段（沒給的區段由 Parse 補成空的）。</summary>
    private static string Content(params (string Section, string[] Blocks)[] sections) =>
        string.Join("\n\n", sections.Select(s => $"## {s.Section}\n\n{string.Join("\n\n", s.Blocks)}"));

    private static string Format(string format, params object[] args) => string.Format(format, args);

    [Fact]
    public async Task Load_ListsNewestFirst_WithVersionTimeAuthorAndReason()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartConversation, Content()),
            Rev(2, ContextNoteRevisionKind.ScribeAuto, Content(), excerpt: "好，那就這樣"),
            Rev(3, ContextNoteRevisionKind.UserEdit, Content()));

        await rig.Vm.LoadAsync(NoteId);

        Assert.Equal([3, 2, 1], rig.Vm.Revisions.Select(r => r.Version));
        var newest = rig.Vm.Revisions[0];
        Assert.Equal(Format(Strings.ContextNote_History_VersionFormat, 3), newest.VersionText);
        Assert.Equal(Strings.ContextNote_Author_User, newest.AuthorText);
        Assert.Equal(Strings.ContextNote_Reason_UserEdit, newest.ReasonText);
        Assert.Equal(Start.AddMinutes(3).ToLocalTime().ToString("MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), newest.TimeText);
    }

    public static TheoryData<ContextNoteRevisionKind, string?, string, string> KindCases() => new()
    {
        { ContextNoteRevisionKind.StartConversation, null, Strings.ContextNote_Author_Start, Strings.ContextNote_Reason_StartConversation },
        { ContextNoteRevisionKind.StartBlank, null, Strings.ContextNote_Author_Start, Strings.ContextNote_Reason_StartBlank },
        { ContextNoteRevisionKind.ScribeFirst, null, Strings.ContextNote_Author_Scribe, Strings.ContextNote_Reason_ScribeFirst },
        { ContextNoteRevisionKind.ScribeAuto, "好，那就這樣", Strings.ContextNote_Author_Scribe, Format(Strings.ContextNote_Reason_ScribeFormat, "好，那就這樣") },
        { ContextNoteRevisionKind.ScribeAuto, null, Strings.ContextNote_Author_Scribe, "" },
        { ContextNoteRevisionKind.ScribeManual, null, Strings.ContextNote_Author_Scribe, Strings.ContextNote_Reason_ScribeManual },
        { ContextNoteRevisionKind.UserEdit, null, Strings.ContextNote_Author_User, Strings.ContextNote_Reason_UserEdit },
        { ContextNoteRevisionKind.UserAction, null, Strings.ContextNote_Author_User, Strings.ContextNote_Reason_UserAction },
        { ContextNoteRevisionKind.UserGoal, null, Strings.ContextNote_Author_User, Strings.ContextNote_Reason_UserGoal },
        { ContextNoteRevisionKind.External, null, Strings.ContextNote_Author_External, Strings.ContextNote_Reason_External },
        { ContextNoteRevisionKind.Revert, "2", Strings.ContextNote_Author_Revert, Format(Strings.ContextNote_Reason_RevertFormat, "2") },
    };

    [Theory]
    [MemberData(nameof(KindCases))]
    public async Task EachKind_ShowsItsAuthorAndReason(ContextNoteRevisionKind kind, string? excerpt, string author, string reason)
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartConversation, Content()),
            Rev(2, kind, Content(), excerpt: excerpt));

        await rig.Vm.LoadAsync(NoteId);

        var row = rig.Vm.Revisions[0];
        Assert.Equal(author, row.AuthorText);
        Assert.Equal(reason, row.ReasonText);
    }

    [Fact]
    public async Task Load_SelectsTheNewestVersionByDefault()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content()),
            Rev(2, ContextNoteRevisionKind.UserEdit, Content()));

        await rig.Vm.LoadAsync(NoteId);

        Assert.Same(rig.Vm.Revisions[0], rig.Vm.Selected);
        Assert.Equal(Format(Strings.ContextNote_History_CompareFormat, 1, 2), rig.Vm.CompareText);
    }

    [Fact]
    public async Task Diff_ListsOnlyChangedSectionsAndBlocks()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content((ContextNoteDocument.Decided, ["甲的決定"]), (ContextNoteDocument.Open, ["待問的事"]))),
            Rev(2, ContextNoteRevisionKind.UserEdit, Content((ContextNoteDocument.Decided, ["甲的決定", "乙的決定"]), (ContextNoteDocument.Open, ["待問的事"]))));

        await rig.Vm.LoadAsync(NoteId);

        var section = Assert.Single(rig.Vm.Sections);
        Assert.Equal(ContextNoteDocument.Decided, section.Name);
        var row = Assert.Single(section.Rows);
        Assert.Equal(ContextNoteDiffRowKind.Added, row.Kind);
        Assert.Equal(Strings.ContextNote_History_Added, row.KindText);
        Assert.Equal("乙的決定", row.Text);
        Assert.Null(row.Segments);
        Assert.Null(rig.Vm.NoChangeText);
        Assert.Null(rig.Vm.GoalChangedText);
    }

    [Fact]
    public async Task Diff_FirstVersion_IsAllAdded()
    {
        var rig = new Rig();
        await rig.SeedAsync(Rev(1, ContextNoteRevisionKind.StartConversation,
            Content((ContextNoteDocument.Breakdown, ["問題一"]), (ContextNoteDocument.Decided, ["決定一", "決定二"]))));

        await rig.Vm.LoadAsync(NoteId);

        Assert.Equal(2, rig.Vm.Sections.Count);
        var rows = rig.Vm.Sections.SelectMany(s => s.Rows).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, r => Assert.Equal(ContextNoteDiffRowKind.Added, r.Kind));
        Assert.Equal(Format(Strings.ContextNote_History_VersionFormat, 1), rig.Vm.CompareText);
    }

    [Fact]
    public async Task Diff_GoalChanged_ShowsTheNewGoalRow()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content(), goal: "舊目標"),
            Rev(2, ContextNoteRevisionKind.UserGoal, Content(), goal: "新目標"));

        await rig.Vm.LoadAsync(NoteId);

        Assert.Equal(Format(Strings.ContextNote_History_GoalChangedFormat, "新目標"), rig.Vm.GoalChangedText);
        Assert.True(rig.Vm.HasGoalChange);
        Assert.Equal(Strings.ContextNote_History_NoTextChange, rig.Vm.NoChangeText);
    }

    [Fact]
    public async Task Diff_OnlyProtectionChanged_ShowsNoTextChange()
    {
        var rig = new Rig();
        var text = Content((ContextNoteDocument.Decided, ["甲的決定"]));
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, text),
            Rev(2, ContextNoteRevisionKind.UserAction, text, protectedBlocks: ["甲的決定"]));

        await rig.Vm.LoadAsync(NoteId);

        Assert.Empty(rig.Vm.Sections);
        Assert.Equal(Strings.ContextNote_History_NoTextChange, rig.Vm.NoChangeText);
    }

    [Fact]
    public async Task Diff_ModifiedBlock_HasInlineSegmentsForTheInsertedWords()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content((ContextNoteDocument.Decided, ["我們決定先用 Ollama 接本機模型"]))),
            Rev(2, ContextNoteRevisionKind.UserEdit, Content((ContextNoteDocument.Decided, ["我們決定先用 Ollama 相容端點接本機模型"]))));

        await rig.Vm.LoadAsync(NoteId);

        var row = Assert.Single(Assert.Single(rig.Vm.Sections).Rows);
        Assert.Equal(ContextNoteDiffRowKind.Changed, row.Kind);
        Assert.Equal(Strings.ContextNote_History_Changed, row.KindText);
        Assert.Equal("我們決定先用 Ollama 相容端點接本機模型", row.Text);
        Assert.NotNull(row.Segments);
        var added = string.Concat(row.Segments!.Where(s => s.Kind == DiffKind.Added).Select(s => s.Text));
        Assert.Equal("相容端點", added);
    }

    [Fact]
    public async Task Diff_AfterTrimmedVersions_ComparesWithTheNearestOlderVersion()
    {
        var rig = new Rig();
        // 版本 2～4 已經被修剪掉，清單是 1、5、6
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content((ContextNoteDocument.Decided, ["甲"]))),
            Rev(5, ContextNoteRevisionKind.UserEdit, Content((ContextNoteDocument.Decided, ["甲", "乙"]))),
            Rev(6, ContextNoteRevisionKind.UserEdit, Content((ContextNoteDocument.Decided, ["甲", "乙", "丙"]))));

        await rig.Vm.LoadAsync(NoteId);

        Assert.Equal([6, 5, 1], rig.Vm.Revisions.Select(r => r.Version));
        Assert.Equal(Format(Strings.ContextNote_History_CompareFormat, 5, 6), rig.Vm.CompareText);
        Assert.Equal("丙", Assert.Single(Assert.Single(rig.Vm.Sections).Rows).Text);

        rig.Vm.Selected = rig.Vm.Revisions[1];
        await rig.Vm.DiffReady;

        Assert.Equal(Format(Strings.ContextNote_History_CompareFormat, 1, 5), rig.Vm.CompareText);
        Assert.Equal("乙", Assert.Single(Assert.Single(rig.Vm.Sections).Rows).Text);
    }

    [Fact]
    public async Task Revert_LatestVersion_CannotBeReverted()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content()),
            Rev(2, ContextNoteRevisionKind.UserEdit, Content()));
        await rig.Vm.LoadAsync(NoteId);

        Assert.False(rig.Vm.CanRevert);
        Assert.False(rig.Vm.RevertCommand.CanExecute(null));

        rig.Vm.Selected = rig.Vm.Revisions[1];
        await rig.Vm.DiffReady;

        Assert.True(rig.Vm.CanRevert);
        Assert.True(rig.Vm.RevertCommand.CanExecute(null));
        Assert.Equal(Format(Strings.ContextNote_History_RevertFormat, 1), rig.Vm.RevertText);
    }

    [Fact]
    public async Task Revert_AsksForConfirmation_ThenRevertsAndRequestsBack()
    {
        var rig = new Rig();
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content()),
            Rev(2, ContextNoteRevisionKind.UserEdit, Content()));
        await rig.Vm.LoadAsync(NoteId);
        rig.Vm.Selected = rig.Vm.Revisions[1];
        await rig.Vm.DiffReady;

        await rig.Vm.RevertCommand.ExecuteAsync(null);

        var asked = Assert.Single(rig.Confirm.Asked);
        Assert.Equal(Format(Strings.ContextNote_History_RevertConfirmFormat, 1), asked.Message);
        Assert.Equal(Format(Strings.ContextNote_History_RevertFormat, 1), asked.ConfirmText);
        Assert.Contains($"Revert:{NoteId}:1", rig.Notes.Calls);
        Assert.Equal(1, rig.BackCount);
    }

    [Fact]
    public async Task Revert_WhenCancelled_DoesNothing()
    {
        var rig = new Rig();
        rig.Confirm.Answer = false;
        await rig.SeedAsync(
            Rev(1, ContextNoteRevisionKind.StartBlank, Content()),
            Rev(2, ContextNoteRevisionKind.UserEdit, Content()));
        await rig.Vm.LoadAsync(NoteId);
        rig.Vm.Selected = rig.Vm.Revisions[1];
        await rig.Vm.DiffReady;

        await rig.Vm.RevertCommand.ExecuteAsync(null);

        Assert.Single(rig.Confirm.Asked);
        Assert.DoesNotContain(rig.Notes.Calls, c => c.StartsWith("Revert:", StringComparison.Ordinal));
        Assert.Equal(0, rig.BackCount);
    }

    [Fact]
    public async Task Back_RaisesBackRequested()
    {
        var rig = new Rig();
        await rig.SeedAsync(Rev(1, ContextNoteRevisionKind.StartBlank, Content()));
        await rig.Vm.LoadAsync(NoteId);

        rig.Vm.BackCommand.Execute(null);

        Assert.Equal(1, rig.BackCount);
        Assert.Empty(rig.Notes.Calls);
    }
}
