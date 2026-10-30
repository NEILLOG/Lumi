using System.Globalization;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 使用習慣提示的接線（plan/23-usage-cost.md 第 7 節）：回應結束後判斷、重新產生計數、輸入框聚焦時的第 3 條、
/// 送出與切換時收起、按鈕的動作。判斷與紀錄在 <see cref="UsageHintCoordinator"/> 和 <see cref="UsageHintRules"/>，這裡只組輸入。
/// <c>hintCoordinator</c> 是 null 時整個功能關閉，行為與沒有提示之前相同。
/// </summary>
public partial class WorkbenchViewModel
{
    private readonly UsageHintCoordinator? _hints;

    /// <summary>重新產生的次數（只在記憶體）：key 是「對話 id ＋ 回合數」，重開 Lumi 歸零。</summary>
    private readonly Dictionary<string, int> _regenerateCounts = new(StringComparer.Ordinal);

    /// <summary>這次重新產生是第 3 次：回應結束後加入 <c>regenerate</c> 提示（<see cref="QueueHintsAfterRun"/> 用掉）。</summary>
    private bool _regenerateHintDue;

    /// <summary>這則對話打開後，第 3 條已經檢查過（載入或切換對話時重設）。</summary>
    private bool _resumeChecked;

    /// <summary>輸入框上方的提示列（ChatComposer 綁定）。</summary>
    public ComposerHintViewModel Hint { get; } = new();

    /// <summary>送出新訊息、切換或新建對話時：目前顯示的提示收起（記為 Dismissed），換模型的確認區也一起收起。</summary>
    private async Task DismissVisibleHintsAsync()
    {
        await ModelSwitcher.DismissPendingSwitchAsync();
        await Hint.CloseAsync();
    }

    /// <summary>
    /// 輸入框取得焦點（ChatComposer 呼叫）：第 3 條「隔很久回到長對話」。每次打開或切到這則對話只檢查一次；回應中先不檢查，之後再聚焦時才檢查。
    /// </summary>
    public async Task OnComposerFocusedAsync()
    {
        if (_hints is null || _resumeChecked || IsResponding || CurrentConversationId is not { } conversationId)
            return;

        _resumeChecked = true;
        try
        {
            var history = await _store.GetMessagesAsync(conversationId);
            var match = UsageHintRules.OnResume(history.LastOrDefault(m => m.Role == ChatRole.Assistant), ContextUsed, _time.GetUtcNow());
            if (match is null || conversationId != CurrentConversationId)
                return;
            await _hints.TryShowAsync(conversationId, [match], _session.ActiveMode, Hint, RunHintActionAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "判斷隔很久回到對話的提示失敗");
        }
    }

    /// <summary>重新產生（RegenerateAsync 呼叫）：這則對話這個回合的計數 +1；第 3 次時，這次回應結束後加入 <c>regenerate</c> 提示。</summary>
    private void CountRegenerate()
    {
        if (CurrentConversationId is not { } conversationId)
            return;
        var key = conversationId + "#" + Turns.ToString(CultureInfo.InvariantCulture);
        var count = _regenerateCounts.GetValueOrDefault(key) + 1;
        _regenerateCounts[key] = count;
        _regenerateHintDue = count == UsageHintThresholds.RegenerateCount;
    }

    /// <summary>
    /// 回應結束（OnRunFinished 呼叫）：只有正常結束的回應才判斷（失敗與停止不算）。數字在這裡同步取下來，判斷在 Defer 的工作裡做。
    /// </summary>
    private void QueueHintsAfterRun(RunFinishedArgs args)
    {
        var regenerateDue = _regenerateHintDue;
        _regenerateHintDue = false;

        var result = args.Result;
        if (_hints is null || result.Cancelled || result.Unexpected || result.Failure is not null || CurrentConversationId is not { } conversationId)
            return;

        args.Defer(ShowAfterResponseHintsAsync(_hints, conversationId, ContextUsed, ContextWindow, Turns, regenerateDue, _session.ActiveMode));
    }

    private async Task ShowAfterResponseHintsAsync(UsageHintCoordinator hints, string conversationId, int contextUsed, int contextLimit,
        int turns, bool regenerateDue, ChatMode mode)
    {
        try
        {
            var history = await _store.GetMessagesAsync(conversationId);
            var compaction = await _store.GetCompactionStateAsync(conversationId);
            var input = new HintInput(
                history,
                compaction,
                contextUsed,
                contextLimit,
                turns,
                await LastToolDefinitionTokensAsync(conversationId, history),
                history.Count(m => m.Role == ChatRole.User) == 1);

            var matches = UsageHintRules.AfterResponse(input).ToList();
            if (regenerateDue)
                matches.Add(new UsageHintMatch(UsageHintIds.Regenerate, [UsageHintThresholds.RegenerateCount.ToString(CultureInfo.InvariantCulture)]));
            if (matches.Count == 0 || conversationId != CurrentConversationId)
                return;

            await hints.TryShowAsync(conversationId, matches, mode, Hint, RunHintActionAsync);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "判斷使用習慣提示失敗");
        }
    }

    /// <summary>這則對話最近一筆對話請求的工具定義大小（第 6 條）；沒有用量來源或沒有紀錄時是 0。</summary>
    private async Task<int> LastToolDefinitionTokensAsync(string conversationId, IReadOnlyList<ChatMessage> history)
    {
        if (_usageStore is null || history.Count == 0)
            return 0;
        var records = await _usageStore.ListAsync(history[0].CreatedAt, _time.GetUtcNow().AddMinutes(1));
        return records.LastOrDefault(r => r.ConversationId == conversationId && r.Purpose == UsagePurpose.Chat)?.ToolDefinitionTokens ?? 0;
    }

    /// <summary>提示按鈕的動作（<see cref="UsageHintActions"/>）。「繼續」只是記為已處理，沒有動作。</summary>
    private Task RunHintActionAsync(string action)
    {
        switch (action)
        {
            case UsageHintActions.NewConversation:
                return NewConversationAsync();
            case UsageHintActions.CompactAndContinue:
                return CompactAndContinueCommand.CanExecute(null) ? CompactAndContinueCommand.ExecuteAsync(null) : Task.CompletedTask;
            case UsageHintActions.ManageTools:
                _ = _settingsWindow.ShowAsync(new SettingsNavigation(SettingsSection.Tools));
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }
}
