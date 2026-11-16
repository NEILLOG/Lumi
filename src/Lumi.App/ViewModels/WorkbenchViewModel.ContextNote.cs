using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.ContextNotes;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台的研究本入口（M35 包 35J）：工具列的「研究本」按鈕、啟動流程、建議卡片的宿主（<see cref="INoteSuggestionHost"/>）。
/// 目前對話有沒有研究本、建議紀錄是不是已略過，都在 <see cref="RefreshContextNoteStateAsync"/> 查好，之後的讀取都是同步的。
/// </summary>
public partial class WorkbenchViewModel : INoteSuggestionHost
{
    private IContextNoteService? _contextNotes;
    private IContextNoteStore? _contextNoteStore;
    private IContextNoteWindowService? _contextNoteWindow;
    private IContextNoteStartPrompt? _contextNoteStart;

    /// <summary>目前對話有研究本（<see cref="RefreshContextNoteStateAsync"/> 更新）。</summary>
    private bool _hasContextNote;

    /// <summary>目前對話的建議紀錄已經被略過。</summary>
    private bool _suggestionDismissed;

    /// <summary>每次查詢都遞增；比較舊的結果回來時丟掉，避免對話已經換了還被蓋回去。</summary>
    private int _contextNoteVersion;

    /// <summary>建議卡片的狀態改變（含回應開始／結束，卡片的「開研究本」要跟著停用或啟用）。</summary>
    public event EventHandler? SuggestionStateChanged;

    private void InitializeContextNote(IContextNoteService? notes, IContextNoteStore? store, IContextNoteWindowService? window, IContextNoteStartPrompt? start)
    {
        _contextNotes = notes;
        _contextNoteStore = store;
        _contextNoteWindow = window;
        _contextNoteStart = start;
        if (notes is not null)
        {
            // 研究本的變動可能來自背景（書記），切回 UI 執行緒再查
            notes.NoteChanged += (_, args) => _dispatcher.Post(() =>
            {
                if (args.ConversationId == RootConversationId)
                    _ = RefreshContextNoteStateAsync();
            });
        }
    }

    /// <summary>有研究本服務與視窗服務才有這個入口（沒有就整組不顯示）。</summary>
    private bool ContextNoteAvailable => _contextNotes is not null && _contextNoteWindow is not null;

    /// <summary>新建的工具卡片用的建議宿主；沒有研究本服務時是 null，建議卡片不產生。</summary>
    internal INoteSuggestionHost? SuggestionHostForNewCards => ContextNoteAvailable ? this : null;

    // ---- 工具列按鈕 ----

    /// <summary>一般對話才顯示：小精靈的專屬對話、看排程紀錄時不顯示（4.11）。</summary>
    public bool ShowContextNoteButton => ContextNoteAvailable && !IsSpriteConversation && !IsViewingRun;

    /// <summary>目前對話有研究本：按鈕是「on」狀態，按了直接開視窗。</summary>
    public bool HasContextNote => _hasContextNote;

    /// <summary>有研究本時一定能按（打開）；沒有研究本時，回應中停用（C8）；支線裡而且主線沒有研究本也停用（E5）。</summary>
    public bool ContextNoteButtonEnabled => HasContextNote || (!IsResponding && CurrentConversationId is not null && !IsInBranch);

    public string ContextNoteButtonTip => HasContextNote ? Strings.ContextNote_ButtonTipOpen
        : IsInBranch ? Strings.Branch_NoteDisabledTip
        : IsResponding ? Strings.ContextNote_ButtonTipBusy
        : Strings.ContextNote_ButtonTipStart;

    /// <summary>有研究本：打開；沒有：走啟動流程（對話框取得目標與來源）。</summary>
    [RelayCommand(CanExecute = nameof(CanOpenContextNote))]
    private async Task OpenContextNoteAsync()
    {
        if (RootConversationId is not { } id || _contextNoteWindow is null)
            return;
        if (HasContextNote)
        {
            await _contextNoteWindow.ShowAsync(id);
            return;
        }
        await StartContextNoteAsync(string.Empty);
    }

    private bool CanOpenContextNote() => ContextNoteButtonEnabled;

    // ---- 啟動流程：對話框 → 建立 → 開視窗 ----

    /// <summary>
    /// 對話框取得目標與來源；取消就什麼都不做。失敗時顯示在提示列，不關掉對話。
    /// 「建議的目標」由建議卡片帶進來，按鈕開始時是空字串。
    /// </summary>
    private async Task StartContextNoteAsync(string suggestedGoal)
    {
        if (CurrentConversationId is not { } id || IsInBranch || _contextNotes is null || _contextNoteWindow is null || _contextNoteStart is null)
            return;
        try
        {
            var messageCount = (await _store.GetMessagesAsync(id)).Count;
            var modelText = ModelSwitcher.HasCurrent ? ModelSwitcher.CapsuleText : null;
            var request = await _contextNoteStart.PromptAsync(suggestedGoal, messageCount, modelText);
            // 取消；或對話框開著時換了對話：不建立
            if (request is null || id != CurrentConversationId)
                return;

            await _contextNotes.StartAsync(id, request.Goal, request.Mode);
            await RefreshContextNoteStateAsync();
            await _contextNoteWindow.ShowAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "開始研究本失敗");
            Notice = Strings.Workbench_UnexpectedError;
        }
    }

    // ---- 狀態查詢與更新 ----

    /// <summary>
    /// 查目前對話的研究本（<see cref="IContextNoteService.GetViewAsync"/>）與建議紀錄（資料庫），更新按鈕與卡片。
    /// 切換對話、建立研究本、回應結束、研究本變動時呼叫。
    /// </summary>
    private async Task RefreshContextNoteStateAsync()
    {
        var version = Interlocked.Increment(ref _contextNoteVersion);
        var id = CurrentConversationId;
        if (id is null || !ContextNoteAvailable)
        {
            ApplyContextNoteState(has: false, dismissed: false);
            return;
        }

        try
        {
            var noteId = RootConversationId ?? id;   // 支線用主線的研究本（E5）
            var has = await _contextNotes!.GetViewAsync(noteId) is not null;
            var dismissed = !has && _contextNoteStore is not null
                && (await _contextNoteStore.GetSuggestionAsync(noteId))?.DismissedAt is not null;
            if (version != _contextNoteVersion || id != CurrentConversationId)
                return;   // 較新的查詢已經開始，或對話已經換了
            ApplyContextNoteState(has, dismissed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "讀取研究本狀態失敗");
            if (version == _contextNoteVersion && id == CurrentConversationId)
                ApplyContextNoteState(has: false, dismissed: false);
        }
    }

    private void ApplyContextNoteState(bool has, bool dismissed)
    {
        _hasContextNote = has;
        _suggestionDismissed = dismissed;
        OnPropertyChanged(nameof(HasContextNote));
        OnPropertyChanged(nameof(ContextNoteButtonEnabled));
        OnPropertyChanged(nameof(ContextNoteButtonTip));
        OnPropertyChanged(nameof(ShowContextNoteButton));
        OpenContextNoteCommand.NotifyCanExecuteChanged();
        SuggestionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>回應開始或結束：按鈕與卡片的啟用狀態跟著變；回應結束時重新查一次。</summary>
    private void OnContextNoteRespondingChanged(bool responding)
    {
        OnPropertyChanged(nameof(ContextNoteButtonEnabled));
        OnPropertyChanged(nameof(ContextNoteButtonTip));
        OpenContextNoteCommand.NotifyCanExecuteChanged();
        SuggestionStateChanged?.Invoke(this, EventArgs.Empty);
        if (!responding)
            _ = RefreshContextNoteStateAsync();
    }

    /// <summary>
    /// 「ShowContextNoteButton」還取決於是否在看排程紀錄（<see cref="ViewingRun"/>）。那個屬性由 ScheduledRun 的 partial 產生，
    /// 這裡只負責把它的變動轉成按鈕的通知。
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(ViewingRun) or nameof(IsViewingRun))
        {
            OnPropertyChanged(nameof(ShowContextNoteButton));
            RefreshBranchBar();   // 看排程紀錄時路徑列隱藏
        }
    }

    // ---- INoteSuggestionHost ----

    public NoteSuggestionState SuggestionState => _hasContextNote ? NoteSuggestionState.Created
        : _suggestionDismissed ? NoteSuggestionState.Dismissed
        : NoteSuggestionState.Pending;

    /// <summary>回應中不能開始研究本（C8）。</summary>
    public bool CanCreate => !IsResponding && !IsInBranch;

    public Task CreateFromSuggestionAsync(string goal) => CanCreate ? StartContextNoteAsync(goal) : Task.CompletedTask;

    /// <summary>略過：寫進建議紀錄（資料庫），之後重開對話仍然是略過。</summary>
    public async Task DismissSuggestionAsync()
    {
        if (CurrentConversationId is not { } id || _contextNoteStore is null)
            return;
        try
        {
            await _contextNoteStore.DismissSuggestionAsync(id, _time.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "略過研究本建議失敗");
            Notice = Strings.Workbench_UnexpectedError;
            return;
        }
        await RefreshContextNoteStateAsync();
    }

    public Task OpenNoteAsync() =>
        RootConversationId is { } id && _contextNoteWindow is not null ? _contextNoteWindow.ShowAsync(id) : Task.CompletedTask;
}
