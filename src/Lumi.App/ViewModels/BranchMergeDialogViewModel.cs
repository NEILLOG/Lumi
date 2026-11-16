using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Branches;
using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>
/// 帶回對話框的 ViewModel（M36 36H，4.5、4.8「帶回的對話框」）。
/// 步驟由深到淺（C7）：每一步先整理摘要，確認後 <c>MergeAsync</c>，最後一步確認後完成。
/// 完成（全部帶回、或取消）時 <see cref="Completion"/> 得到結果；視窗用 <see cref="Finished"/> 關閉。
/// 所有狀態都在 UI 執行緒上改（測試也在同一條執行緒上呼叫）。
/// </summary>
public sealed partial class BranchMergeDialogViewModel : ObservableObject
{
    private readonly IBranchService _branches;
    private readonly BranchMergeRequest _request;
    private readonly TaskCompletionSource<BranchMergeResult> _completion = new();
    private CancellationTokenSource? _draftCts;
    private Conversation? _lastParent;
    private int _stepIndex;
    private bool _finished;
    private bool _busy;
    private bool _cancelRequested;

    public BranchMergeDialogViewModel(IBranchService branches, BranchMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Steps.Count == 0)
            throw new ArgumentException("帶回至少要有一步。", nameof(request));
        _branches = branches;
        _request = request;
        RefreshStep();

        ConfirmCommand = new AsyncRelayCommand(ConfirmAsync, () => CanConfirm);
        RedraftCommand = new AsyncRelayCommand(RedraftAsync, () => CanRedraft);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// <summary>完成（全部帶回或取消）時得到結果；取消時 <see cref="BranchMergeResult.OpenTo"/> 依 4.5 決定。</summary>
    public Task<BranchMergeResult> Completion => _completion.Task;

    /// <summary>完成時發生（視窗收到後關閉）。</summary>
    public event EventHandler? Finished;

    public IAsyncRelayCommand ConfirmCommand { get; }

    public IAsyncRelayCommand RedraftCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>目前這一步的支線 id 與目標；由深到淺。</summary>
    private BranchMergeStep CurrentStep => _request.Steps[_stepIndex];

    [ObservableProperty]
    private string _title = "";

    /// <summary>兩步時才有（「第 1 步，共 2 步…」）。</summary>
    [ObservableProperty]
    private string _stepText = "";

    [ObservableProperty]
    private bool _showStepText;

    [ObservableProperty]
    private string _explainText = "";

    /// <summary>ModelText 是 null 時不顯示。</summary>
    [ObservableProperty]
    private string _costText = "";

    [ObservableProperty]
    private bool _hasCost;

    [ObservableProperty]
    private string _primaryText = "";

    [ObservableProperty]
    private string _summaryPlaceholder = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private string _tooLongText = "";

    [ObservableProperty]
    private bool _isDrafting;

    /// <summary>帶回的摘要（可以修改）；整理中是空的，輸入框停用。</summary>
    [ObservableProperty]
    private string _summaryText = "";

    public bool HasError => ErrorText.Length > 0;

    public bool HasTooLong => TooLongText.Length > 0;

    /// <summary>去頭尾空白後的字數（和 Core 的上限檢查一致）。</summary>
    public int SummaryLength => SummaryText.Trim().Length;

    public bool CanConfirm => !IsDrafting && !_busy && !_finished && SummaryLength > 0 && SummaryLength <= BranchLimits.SummaryMaxChars;

    public bool CanRedraft => !IsDrafting && !_busy && !_finished;

    /// <summary>開始第一步並整理摘要。整理失敗不丟例外，只顯示錯誤文字。</summary>
    public Task StartAsync() => DraftAsync();

    partial void OnSummaryTextChanged(string value) => OnSummaryChanged();

    partial void OnIsDraftingChanged(bool value)
    {
        SummaryPlaceholder = value ? Strings.Branch_MergeDrafting : "";
        NotifyCommands();
    }

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnTooLongTextChanged(string value) => OnPropertyChanged(nameof(HasTooLong));

    /// <summary>重新整理一次（覆蓋目前的文字，不再確認）。</summary>
    public Task RedraftAsync() => DraftAsync();

    /// <summary>確認目前這一步：帶回；還有下一步就整理下一步，否則完成。</summary>
    public async Task ConfirmAsync()
    {
        if (!CanConfirm)
            return;

        var summary = SummaryText.Trim();
        _busy = true;
        NotifyCommands();
        try
        {
            _lastParent = await _branches.MergeAsync(CurrentStep.ConversationId, summary);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return;
        }
        finally
        {
            _busy = false;
            NotifyCommands();
        }

        // 合併期間按了取消：這一步已經帶回，照取消結束
        if (_cancelRequested || _stepIndex + 1 >= _request.Steps.Count)
        {
            Complete();
            return;
        }

        _stepIndex++;
        RefreshStep();
        await DraftAsync();
    }

    /// <summary>
    /// 取消：沒有進行中的合併就立刻結束（停止整理）；合併進行中則等它結束再結束。
    /// 結果的 <c>OpenTo</c> 是最後一條已帶回支線的上一層（一步都沒帶回是 null）。
    /// </summary>
    public void Cancel()
    {
        if (_finished)
            return;
        if (_busy)
        {
            _cancelRequested = true;
            return;
        }
        Complete();
    }

    private async Task DraftAsync()
    {
        _draftCts?.Cancel();
        _draftCts?.Dispose();
        var cts = new CancellationTokenSource();
        _draftCts = cts;

        ErrorText = "";
        SummaryText = "";
        IsDrafting = true;
        try
        {
            var draft = await _branches.DraftSummaryAsync(CurrentStep.ConversationId, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            if (draft.Text is { } text)
                SummaryText = text;
            else if (draft.NoModel)
                ErrorText = Strings.Branch_MergeNoModel;
            else
                ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeFailedFormat,
                    (draft.Failure?.Message ?? "").TrimEnd('。'));
        }
        catch (OperationCanceledException)
        {
            // 取消：照常結束，不顯示錯誤
        }
        catch (Exception ex)
        {
            ErrorText = string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeFailedFormat, ex.Message.TrimEnd('。'));
        }
        finally
        {
            if (ReferenceEquals(_draftCts, cts))
                IsDrafting = false;
        }
    }

    private void Complete()
    {
        if (_finished)
            return;
        _finished = true;
        _draftCts?.Cancel();
        _completion.TrySetResult(new BranchMergeResult(_lastParent));
        NotifyCommands();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>依目前的步驟更新標題、說明、費用與主要按鈕的文字。</summary>
    private void RefreshStep()
    {
        var step = CurrentStep;
        var isLast = _stepIndex + 1 >= _request.Steps.Count;

        Title = step.TargetIsMain ? Strings.Branch_MergeDialogTitle : Strings.Branch_MergeDialogTitleUp;
        ShowStepText = _request.Steps.Count > 1;
        StepText = ShowStepText
            ? string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeStepFormat,
                _stepIndex + 1, _request.Steps.Count, step.Title, step.TargetTitle)
            : "";
        ExplainText = string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeExplainFormat, step.MessageCount);

        HasCost = _request.ModelText is not null;
        CostText = HasCost
            ? string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeCostFormat, _request.ModelText, _request.CostConversationTitle)
            : "";

        PrimaryText = !isLast
            ? Strings.Branch_MergeNext
            : step.TargetIsMain ? Strings.Branch_Merge : Strings.Branch_MergeUp;
    }

    private void OnSummaryChanged()
    {
        var length = SummaryLength;
        TooLongText = length > BranchLimits.SummaryMaxChars
            ? string.Format(CultureInfo.CurrentCulture, Strings.Branch_MergeTooLongFormat, BranchLimits.SummaryMaxChars, length)
            : "";
        OnPropertyChanged(nameof(SummaryLength));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        ConfirmCommand.NotifyCanExecuteChanged();
        RedraftCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(CanRedraft));
    }
}
