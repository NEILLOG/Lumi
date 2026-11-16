using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>研究本的版本歷史（差異、退回）。計畫 4.11「版本歷史」、6.6「包 35I」。</summary>
public sealed partial class ContextNoteHistoryViewModel : ObservableObject
{
    private readonly IContextNoteStore _store;
    private readonly IContextNoteService _notes;
    private readonly IConfirmService _confirm;
    private string _noteId = string.Empty;
    private int _currentVersion;
    private int _diffSeq;
    private Task _diffTask = Task.CompletedTask;

    public ContextNoteHistoryViewModel(IContextNoteStore store, IContextNoteService notes, IConfirmService confirm)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(confirm);
        _store = store;
        _notes = notes;
        _confirm = confirm;
    }

    /// <summary>版本清單，由新到舊。</summary>
    public ObservableCollection<ContextNoteRevisionRow> Revisions { get; } = [];

    /// <summary>目前這一版和前一版的差異（只含有變的區段）。</summary>
    public ObservableCollection<ContextNoteDiffSection> Sections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevertText), nameof(CanRevert))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
    private ContextNoteRevisionRow? _selected;

    /// <summary>標題：「第 N 版和第 M 版的差異」；沒有前一版時只有版本文字。</summary>
    [ObservableProperty]
    private string _compareText = string.Empty;

    /// <summary>差異最上面的一列：目標改成什麼；目標沒變或沒有前一版時是 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoalChange))]
    private string? _goalChangedText;

    public bool HasGoalChange => GoalChangedText is not null;

    /// <summary>內容完全相同時的說明；有差異或沒有前一版時是 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChange))]
    private string? _noChangeText;

    public bool HasNoChange => NoChangeText is not null;

    /// <summary>退回按鈕的文字，例如「退回到第 6 版」。</summary>
    public string RevertText => Selected is { } row
        ? string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_History_RevertFormat, row.Version)
        : string.Empty;

    /// <summary>最新一版（研究本目前的版本）不能退回。</summary>
    public bool CanRevert => Selected is { } row && row.Version != _currentVersion;

    /// <summary>目前這次差異載入的 Task（測試與等待用）。</summary>
    public Task DiffReady => _diffTask;

    /// <summary>按了「回到研究本」，或退回完成。</summary>
    public event EventHandler? BackRequested;

    /// <summary>載入版本清單，預設選最新一版。</summary>
    public async Task LoadAsync(string noteId)
    {
        _noteId = noteId;
        var note = await _store.GetAsync(noteId);
        var infos = await _store.ListRevisionsAsync(noteId);
        _currentVersion = note?.CurrentVersion ?? infos.Select(i => i.Version).DefaultIfEmpty(0).Max();

        Revisions.Clear();
        foreach (var info in infos)
            Revisions.Add(ToRow(info));

        Selected = null;
        Selected = Revisions.FirstOrDefault();
        await _diffTask;
    }

    partial void OnSelectedChanged(ContextNoteRevisionRow? value) => _diffTask = ShowDiffAsync(value);

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync()
    {
        if (Selected is not { } row)
            return;

        var version = row.Version;
        var asked = await _confirm.ConfirmAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_History_RevertConfirmFormat, version),
            string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_History_RevertFormat, version));
        if (!asked)
            return;

        await _notes.RevertAsync(_noteId, version);
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowDiffAsync(ContextNoteRevisionRow? row)
    {
        var seq = ++_diffSeq;
        if (row is null)
        {
            Sections.Clear();
            GoalChangedText = null;
            NoChangeText = null;
            CompareText = string.Empty;
            return;
        }

        var version = row.Version;
        // 前一版：清單裡比這一版小、最大的那一版（版本被修剪後中間會缺號，所以不是直接 version - 1）
        var previousVersion = Revisions.Where(r => r.Version < version).Select(r => (int?)r.Version).Max();
        var current = await _store.GetRevisionAsync(_noteId, version);
        var previous = previousVersion is { } p ? await _store.GetRevisionAsync(_noteId, p) : null;
        if (seq != _diffSeq)
            return;

        var oldDocument = previous is null ? ContextNoteDocument.Empty : ContextNoteDocument.Parse(previous.Content);
        var newDocument = current is null ? ContextNoteDocument.Empty : ContextNoteDocument.Parse(current.Content);
        var sections = ContextNoteDiff.Compute(oldDocument, newDocument)
            .Where(s => s.HasChanges)
            .Select(s => new ContextNoteDiffSection(s.Name, s.Blocks.Where(b => b.Kind != DiffKind.Same).Select(ToDiffRow).ToArray()))
            .ToArray();

        Sections.Clear();
        foreach (var section in sections)
            Sections.Add(section);

        CompareText = previousVersion is { } pv
            ? string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_History_CompareFormat, pv, version)
            : row.VersionText;
        GoalChangedText = previous is not null && current is not null && previous.Goal != current.Goal
            ? string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_History_GoalChangedFormat, current.Goal)
            : null;
        NoChangeText = previous is not null && sections.Length == 0
            ? Strings.ContextNote_History_NoTextChange
            : null;
    }

    private static ContextNoteDiffRow ToDiffRow(DiffBlock block) => block.Kind switch
    {
        DiffKind.Added => new ContextNoteDiffRow(ContextNoteDiffRowKind.Added, Strings.ContextNote_History_Added, block.NewText ?? string.Empty, null),
        DiffKind.Removed => new ContextNoteDiffRow(ContextNoteDiffRowKind.Removed, Strings.ContextNote_History_Removed, block.OldText ?? string.Empty, null),
        _ => new ContextNoteDiffRow(ContextNoteDiffRowKind.Changed, Strings.ContextNote_History_Changed, block.NewText ?? string.Empty, block.Inline),
    };

    private static ContextNoteRevisionRow ToRow(ContextNoteRevisionInfo info)
    {
        var (author, reason) = info.Kind switch
        {
            ContextNoteRevisionKind.StartConversation => (Strings.ContextNote_Author_Start, Strings.ContextNote_Reason_StartConversation),
            ContextNoteRevisionKind.StartBlank => (Strings.ContextNote_Author_Start, Strings.ContextNote_Reason_StartBlank),
            ContextNoteRevisionKind.ScribeFirst => (Strings.ContextNote_Author_Scribe, Strings.ContextNote_Reason_ScribeFirst),
            ContextNoteRevisionKind.ScribeAuto => (Strings.ContextNote_Author_Scribe,
                string.IsNullOrEmpty(info.TriggerExcerpt) ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Reason_ScribeFormat, info.TriggerExcerpt)),
            ContextNoteRevisionKind.ScribeManual => (Strings.ContextNote_Author_Scribe, Strings.ContextNote_Reason_ScribeManual),
            ContextNoteRevisionKind.UserEdit => (Strings.ContextNote_Author_User, Strings.ContextNote_Reason_UserEdit),
            ContextNoteRevisionKind.UserAction => (Strings.ContextNote_Author_User, Strings.ContextNote_Reason_UserAction),
            ContextNoteRevisionKind.UserGoal => (Strings.ContextNote_Author_User, Strings.ContextNote_Reason_UserGoal),
            ContextNoteRevisionKind.External => (Strings.ContextNote_Author_External, Strings.ContextNote_Reason_External),
            ContextNoteRevisionKind.Revert => (Strings.ContextNote_Author_Revert,
                string.IsNullOrEmpty(info.TriggerExcerpt) ? string.Empty : string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Reason_RevertFormat, info.TriggerExcerpt)),
            _ => (string.Empty, string.Empty),
        };
        return new ContextNoteRevisionRow(
            info.Version,
            info.Kind,
            string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_History_VersionFormat, info.Version),
            author,
            info.CreatedAt.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture),
            reason);
    }
}
