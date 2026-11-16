using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>
/// 研究本視窗的 ViewModel（M35 35G）：永遠顯示工作台目前那則對話的研究本。
/// 事件可能從背景執行緒來，一律用 <see cref="IUiDispatcher"/> 回到 UI 執行緒再更新。
/// </summary>
public sealed partial class ContextNoteViewModel : ObservableObject, IDisposable
{
    private readonly IContextNoteService _notes;
    private readonly IContextNoteFiles _files;
    private readonly IConversationStore _conversations;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFolderOpener _folders;
    private readonly IFilePickerService _picker;
    private readonly TimeProvider _time;
    private ContextNoteView? _view;
    private string? _conversationTitle;
    private bool _applyingAutoOrganize;
    private int _generation;
    private bool _disposed;

    public ContextNoteViewModel(IContextNoteService notes, IContextNoteFiles files, IConversationStore conversations,
        IUiDispatcher dispatcher, IFolderOpener folders, IFilePickerService picker, TimeProvider time,
        ContextNoteEditorViewModel editor, ContextNoteHistoryViewModel history)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(history);
        _notes = notes;
        _files = files;
        _conversations = conversations;
        _dispatcher = dispatcher;
        _folders = folders;
        _picker = picker;
        _time = time;
        Editor = editor;
        History = history;
        _windowTitle = Strings.ContextNote_Window_Title;

        notes.NoteChanged += OnNoteChanged;
        notes.StatusChanged += OnStatusChanged;
        editor.Finished += OnEditorFinished;
        history.BackRequested += OnHistoryBackRequested;
    }

    /// <summary>編輯器（35H）；視窗把它的控制項的 DataContext 設成這個。</summary>
    public ContextNoteEditorViewModel Editor { get; }

    /// <summary>版本歷史（35I）；視窗把它的控制項的 DataContext 設成這個。</summary>
    public ContextNoteHistoryViewModel History { get; }

    public string? NoteId { get; private set; }

    public string? ConversationId { get; private set; }

    public bool HasNote => NoteId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreview), nameof(IsEdit), nameof(IsHistory))]
    private ContextNoteWindowMode _mode = ContextNoteWindowMode.Preview;

    public bool IsPreview => Mode == ContextNoteWindowMode.Preview;

    public bool IsEdit => Mode == ContextNoteWindowMode.Edit;

    public bool IsHistory => Mode == ContextNoteWindowMode.History;

    [ObservableProperty]
    private string _windowTitle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGoalCommand))]
    private string _goal = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGoalCommand))]
    private bool _isEditingGoal;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveGoalCommand))]
    private string _goalDraft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGoalHint), nameof(ShowHintRow))]
    private string? _goalHint;

    public bool HasGoalHint => !string.IsNullOrEmpty(GoalHint);

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHintRow))]
    private string? _statusDetailText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHintRow))]
    private bool _showRetry;

    [ObservableProperty]
    private bool _isOrganizing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestored), nameof(ShowHintRow))]
    private string? _restoredText;

    public bool HasRestored => !string.IsNullOrEmpty(RestoredText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHintRow))]
    private bool _showLongNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice), nameof(ShowHintRow))]
    private string? _notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    /// <summary>提示列要不要出現（目標提示、整理失敗、放回說明、很長的提醒、匯出結果任一有就顯示）。</summary>
    public bool ShowHintRow => HasGoalHint || ShowRetry || HasRestored || ShowLongNote || HasNotice;

    [ObservableProperty]
    private bool _autoOrganize;

    /// <summary>由視窗服務設定（工作台螢幕夠寬才能並排）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SideBySideTip))]
    private bool _canSideBySide;

    public string SideBySideTip => CanSideBySide ? Strings.ContextNote_SideBySideTip : Strings.ContextNote_SideBySideTooNarrow;

    [ObservableProperty]
    private IReadOnlyList<ContextNoteSectionViewModel> _sections = [];

    partial void OnAutoOrganizeChanged(bool value)
    {
        if (_applyingAutoOrganize || NoteId is not { } noteId)
            return;
        _ = SetAutoOrganizeAsync(noteId, value);
    }

    /// <summary>開啟或換到一則對話：先同步外部修改（有研究本時），再讀取目前內容。</summary>
    public async Task LoadAsync(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        var generation = ++_generation;
        ConversationId = conversationId;
        var view = await _notes.GetViewAsync(conversationId);
        if (view is not null)
        {
            await _notes.SyncExternalAsync(view.Note.Id);
            if (generation != _generation)
                return;
            view = await _notes.GetViewAsync(conversationId);
        }
        var conversation = await _conversations.GetConversationAsync(conversationId);
        if (generation != _generation)
            return;
        if (view?.Note.Id != NoteId && Mode != ContextNoteWindowMode.Preview)
            Mode = ContextNoteWindowMode.Preview;
        Apply(view, conversation?.Title);
    }

    /// <summary>視窗啟用時呼叫：同步外部修改（編輯中不做，避免和正在存的內容打架）。</summary>
    public async Task SyncExternalAsync()
    {
        if (NoteId is not { } noteId || Mode == ContextNoteWindowMode.Edit)
            return;
        await _notes.SyncExternalAsync(noteId);
        await RefreshAsync();
    }

    /// <summary>
    /// 離開編輯（換對話或關閉視窗前）：先存檔；超過字數或存檔丟例外就不存，直接結束編輯（都會結束 BeginUserEdit）。
    /// </summary>
    public async Task LeaveEditAsync()
    {
        if (Mode != ContextNoteWindowMode.Edit)
            return;
        bool saved;
        try
        {
            saved = await Editor.CommitAsync();
        }
        catch (Exception)
        {
            saved = false;
        }
        if (!saved)
            await EndEditWithoutSavingAsync();
    }

    /// <summary>結束編輯但不存檔（結束 BeginUserEdit 時內容不變）。</summary>
    public async Task EndEditWithoutSavingAsync()
    {
        if (NoteId is { } noteId)
            await _notes.EndUserEditAsync(noteId, null);
        Mode = ContextNoteWindowMode.Preview;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notes.NoteChanged -= OnNoteChanged;
        _notes.StatusChanged -= OnStatusChanged;
        Editor.Finished -= OnEditorFinished;
        History.BackRequested -= OnHistoryBackRequested;
    }

    [RelayCommand]
    private void EnterEdit()
    {
        if (Mode == ContextNoteWindowMode.Edit || NoteId is not { } noteId || _view is null)
            return;
        Editor.Begin(noteId, _view.Current.Content);
        Mode = ContextNoteWindowMode.Edit;
    }

    [RelayCommand]
    private async Task ShowPreviewAsync()
    {
        if (Mode == ContextNoteWindowMode.Preview)
            return;
        if (Mode == ContextNoteWindowMode.Edit && !await Editor.CommitAsync())
        {
            // 超過字數：留在編輯。切換鈕是單選群組，要把它拉回「編輯」
            OnPropertyChanged(nameof(IsPreview));
            OnPropertyChanged(nameof(IsEdit));
            return;
        }
        await ReturnToPreviewAsync();
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        if (NoteId is not { } noteId)
            return;
        if (Mode == ContextNoteWindowMode.Edit && !await Editor.CommitAsync())
            return;
        Mode = ContextNoteWindowMode.History;
        await History.LoadAsync(noteId);
    }

    [RelayCommand]
    private void EditGoal()
    {
        GoalDraft = Goal;
        IsEditingGoal = true;
    }

    [RelayCommand(CanExecute = nameof(CanSaveGoal))]
    private async Task SaveGoalAsync()
    {
        if (NoteId is not { } noteId)
            return;
        var goal = GoalDraft.Trim();
        await _notes.SetGoalAsync(noteId, goal);
        Goal = goal;
        IsEditingGoal = false;
    }

    private bool CanSaveGoal() => IsEditingGoal && IsGoalValid(GoalDraft);

    [RelayCommand]
    private void CancelGoal() => IsEditingGoal = false;

    /// <summary>目標提示的「修改目標」：進入修改目標（和按下「修改目標」一樣）。</summary>
    [RelayCommand]
    private void AcceptGoalHint() => EditGoal();

    [RelayCommand]
    private async Task DismissGoalHintAsync()
    {
        if (NoteId is not { } noteId)
            return;
        await _notes.DismissGoalHintAsync(noteId);
        GoalHint = null;
    }

    [RelayCommand]
    private async Task OrganizeNowAsync()
    {
        if (NoteId is not { } noteId)
            return;
        await _notes.OrganizeNowAsync(noteId);
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (NoteId is not { } noteId)
            return;
        await _folders.OpenAsync(_files.GetFolder(noteId));
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (NoteId is not { } noteId)
            return;
        var name = string.Concat((_conversationTitle ?? "").Split(Path.GetInvalidFileNameChars()));
        var destination = await _picker.SaveFileAsync($"{name}-{Strings.ContextNote_Window_Title}", "zip");
        if (destination is null)
            return;
        try
        {
            await _files.ExportZipAsync(noteId, destination);
            Notice = string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_ExportDoneFormat, destination);
        }
        catch (Exception)
        {
            Notice = Strings.ContextNote_ExportFailed;
        }
    }

    internal static bool IsGoalValid(string draft)
    {
        var trimmed = draft.Trim();
        return trimmed.Length > 0 && trimmed.Length <= ContextNoteLimits.GoalMaxLength;
    }

    private async Task ReturnToPreviewAsync()
    {
        Mode = ContextNoteWindowMode.Preview;
        await RefreshAsync();
    }

    /// <summary>重新讀取目前這本研究本（不同步外部修改）。</summary>
    private async Task RefreshAsync()
    {
        if (ConversationId is not { } conversationId)
            return;
        var generation = ++_generation;
        var view = await _notes.GetViewAsync(conversationId);
        if (generation != _generation)
            return;
        Apply(view, _conversationTitle);
    }

    private async Task SetAutoOrganizeAsync(string noteId, bool value)
    {
        await _notes.SetAutoOrganizeAsync(noteId, value);
    }

    private void Apply(ContextNoteView? view, string? conversationTitle)
    {
        _view = view;
        _conversationTitle = conversationTitle;
        WindowTitle = conversationTitle is null
            ? Strings.ContextNote_Window_Title
            : string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Window_TitleFormat, conversationTitle);
        NoteId = view?.Note.Id;

        if (view is null)
        {
            Goal = "";
            GoalHint = null;
            RestoredText = null;
            ShowLongNote = false;
            Sections = [];
            IsOrganizing = false;
            ShowRetry = false;
            StatusDetailText = null;
            StatusText = "";
            return;
        }

        Goal = view.Note.Goal;
        GoalHint = view.Note.GoalHint;
        _applyingAutoOrganize = true;
        try
        {
            AutoOrganize = view.Note.AutoOrganize;
        }
        finally
        {
            _applyingAutoOrganize = false;
        }
        ShowLongNote = view.Current.Content.Length > ContextNoteLimits.NoteLongChars;
        RestoredText = view.Current.RestoredCount > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Status_RestoredFormat, view.Current.RestoredCount)
            : null;
        Sections = BuildSections(view);
        ApplyStatus(_notes.GetStatus(view.Note.Id));
    }

    private IReadOnlyList<ContextNoteSectionViewModel> BuildSections(ContextNoteView view)
    {
        var noteId = view.Note.Id;
        var current = ContextNoteDocument.Parse(view.Current.Content);
        var fromScribe = view.Current.Kind is ContextNoteRevisionKind.ScribeFirst or ContextNoteRevisionKind.ScribeAuto or ContextNoteRevisionKind.ScribeManual;
        var updated = view.Previous is not null && fromScribe
            ? ContextNoteDiff.ChangedBlocks(ContextNoteDocument.Parse(view.Previous.Content), current)
            : new HashSet<string>(StringComparer.Ordinal);
        var protectedBlocks = view.Current.ProtectedBlocks.ToHashSet(StringComparer.Ordinal);

        return ContextNoteDocument.SectionNames.Select(name =>
        {
            var blocks = current.Section(name).Blocks
                .Select(block => new ContextNoteBlockViewModel(noteId, name, block.Text,
                    updated.Contains(block.Text), protectedBlocks.Contains(block.Text), _notes, _files))
                .ToList();
            return new ContextNoteSectionViewModel(name, blocks);
        }).ToList();
    }

    /// <summary>狀態文字與提示：Idle、Failed、Rejected、NoModel 時狀態列仍顯示版本文字，失敗的說明另外放在提示列。</summary>
    private void ApplyStatus(ContextNoteStatus status)
    {
        IsOrganizing = status.State == ScribeState.Running;
        StatusText = status.State switch
        {
            ScribeState.Running => Strings.ContextNote_Status_Running,
            ScribeState.WaitingForUser => Strings.ContextNote_Status_Waiting,
            ScribeState.Paused => Strings.ContextNote_Status_Paused,
            _ => VersionText(),
        };
        ShowRetry = status.State is ScribeState.Failed or ScribeState.Rejected or ScribeState.NoModel;
        StatusDetailText = status.State switch
        {
            ScribeState.Failed => Strings.ContextNote_Status_Failed,
            ScribeState.Rejected => Strings.ContextNote_Status_Rejected,
            ScribeState.NoModel => Strings.ContextNote_Status_NoModel,
            _ => null,
        };
    }

    private string VersionText()
    {
        if (_view is null)
            return "";
        var local = TimeZoneInfo.ConvertTime(_view.Note.UpdatedAt, _time.LocalTimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
        return string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Status_UpdatedFormat, _view.Current.Version, local);
    }

    private void OnNoteChanged(object? sender, ContextNoteChangedArgs args) =>
        _dispatcher.Post(() =>
        {
            if (args.NoteId == NoteId)
                _ = RefreshAsync();
        });

    private void OnStatusChanged(object? sender, ContextNoteStatus status) =>
        _dispatcher.Post(() =>
        {
            if (status.NoteId == NoteId)
                ApplyStatus(status);
        });

    private void OnEditorFinished(object? sender, EventArgs e) => _ = ReturnToPreviewAsync();

    private void OnHistoryBackRequested(object? sender, EventArgs e) => _ = ReturnToPreviewAsync();
}
