using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 上下文整理的畫面（M22 22I）：整理中提示、摘要分隔線（整理完成後重讀訊息算位置）、整理失敗提示、
/// 用量面板的「整理並繼續」（手動整理）。整理本身在 <see cref="IChatSession"/>；摘要用量的累計在 WorkbenchViewModel.Usage.cs。
/// </summary>
public partial class WorkbenchViewModel
{
    /// <summary>正在整理（自動或手動的摘要呼叫進行中）：訊息區底部顯示「正在整理對話…」，送出、重試與重新產生停用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCompactAndContinue))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(RetryCommand), nameof(RegenerateCommand), nameof(CompactAndContinueCommand))]
    private bool _isCompacting;

    /// <summary>整理失敗的提示（訊息區底部，不擋繼續送出）；按「知道了」、下一次整理開始或重讀對話時拿掉。</summary>
    [ObservableProperty]
    private bool _hasCompactionFailure;

    /// <summary>整理完成後要重讀訊息（分隔線的位置要從資料庫算）。回應中先記下，回應結束時再重讀。</summary>
    private bool _reloadAfterCompaction;

    /// <summary>用量面板的「整理並繼續」：沒有回應進行中、沒有在整理、有對話。</summary>
    public bool CanCompactAndContinue => !IsResponding && !IsCompacting && CurrentConversationId is not null;

    /// <summary>手動整理（用量面板，快滿時）：直接做摘要，完成後重讀訊息顯示分隔線。</summary>
    [RelayCommand(CanExecute = nameof(CanCompactNow))]
    private async Task CompactAndContinueAsync()
    {
        var conversationId = CurrentConversationId!;
        try
        {
            await _session.CompactAsync();   // 結果由 CompactionStarted／Completed／Failed 事件呈現
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "無法手動整理對話 {ConversationId}", conversationId);   // 回應中或沒有對話：按鈕已停用，這裡只是保險
        }
        finally
        {
            IsCompacting = false;   // 被停止時只有 Started、沒有結束事件（22H）
        }
        await ReloadAfterCompactionAsync(conversationId);
    }

    private bool CanCompactNow() => CanCompactAndContinue;

    [RelayCommand]
    private void DismissCompactionFailure() => HasCompactionFailure = false;

    /// <summary>整理的事件（畫面部分）：由 <see cref="OnEventReceived"/> 呼叫。第一層（工具結果）瞬間完成，不顯示任何東西。</summary>
    private void OnCompactionDisplayEvent(ChatEvent chatEvent)
    {
        switch (chatEvent)
        {
            case CompactionStarted { Kind: CompactionKind.Summary }:
                HasCompactionFailure = false;
                IsCompacting = true;
                break;
            case CompactionCompleted { Kind: CompactionKind.Summary }:
                IsCompacting = false;
                _reloadAfterCompaction = true;
                break;
            case CompactionFailed { Kind: CompactionKind.Summary }:
                IsCompacting = false;
                HasCompactionFailure = true;
                break;
        }
    }

    /// <summary>
    /// 回應結束（<see cref="OnRunFinished"/> 呼叫）：整理若被停止就沒有結束事件，這裡一併清掉；
    /// 整理在回應中完成的，延後到回應結束後重讀訊息（不在回應中間替換畫面上的泡泡與卡片）。
    /// </summary>
    private void OnCompactionRunFinished(RunFinishedArgs args)
    {
        IsCompacting = false;
        if (_reloadAfterCompaction && CurrentConversationId is { } conversationId)
            args.Defer(ReloadAfterCompactionAsync(conversationId));
    }

    /// <summary>整理後下一次請求的估算（只有輸入，沒有輸出）：套用整理後的歷史，加上這則對話固定的 system prompt 與工具定義。</summary>
    private async Task<TokenUsage> EstimateCompactedUsageAsync(string conversationId, IReadOnlyList<ChatMessage> messages, CompactionState compaction)
    {
        var overhead = ContextEstimator.EstimateText(await _store.GetSystemPromptAsync(conversationId))
            + ContextEstimator.EstimateText(await _store.GetToolSnapshotAsync(conversationId));
        var estimate = ContextEstimator.Estimate(CompactionApplier.Apply(messages, compaction), compaction, overhead);
        return new TokenUsage(estimate, 0, 0, 0);
    }

    /// <summary>整理完成後重讀訊息（只在還是當初那則對話時）。</summary>
    private async Task ReloadAfterCompactionAsync(string conversationId)
    {
        if (!_reloadAfterCompaction)
            return;
        _reloadAfterCompaction = false;
        if (conversationId == CurrentConversationId)
            await ReloadMessagesAsync();
    }
}
