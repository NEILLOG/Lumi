using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台的本對話用量（用量圖示與面板）：累計由 <see cref="ConversationUsageTally"/> 維護，
/// 這裡負責把會話事件與目前模型、連線狀態接到 <see cref="ConversationUsageViewModel"/>。
/// </summary>
public partial class WorkbenchViewModel
{
    private readonly ConversationUsageTally _tally = new();
    private readonly IConnectionTester? _tester;
    private bool _retryingConnection;
    private bool _inCompaction;   // 上下文整理的摘要呼叫進行中（M22）：它的用量事件不是對話回應的用量
    private readonly IUsageStore? _usageStore;     // 費用（M23 23H）；null 時用量面板不顯示費用段落
    private readonly BudgetMonitor? _budget;

    /// <summary>每月預算的提醒列（工作台頂端，在「有新版」與通知列之間）；提醒由 <c>BudgetAlertCoordinator</c> 推進來。</summary>
    public BudgetAlertViewModel BudgetAlert { get; }

    /// <summary>本對話用量（頂端用量圖示與面板綁定這裡）。</summary>
    public ConversationUsageViewModel Usage { get; }

    /// <summary>用量面板的「用量與費用 ›」與預算提醒的「查看用量」：開設定視窗到用量與費用分類。</summary>
    private void OpenUsagePage() => _ = _settingsWindow.ShowAsync(new SettingsNavigation(SettingsSection.Usage));

    /// <summary>
    /// 費用段落（M23 23H）：從資料庫查本對話與本月的費用，推給 <see cref="Usage"/>。
    /// 只在載入對話、回應結束、上下文整理完成時呼叫；串流中費用還沒寫入，不更新。沒有費用來源時什麼都不做。
    /// </summary>
    private async Task RefreshCostAsync()
    {
        if (!Usage.ShowCost || _usageStore is null || _budget is null)
            return;

        var conversationId = CurrentConversationId;
        try
        {
            var conversation = conversationId is null
                ? ConversationCost.Empty
                : await _usageStore.GetConversationCostAsync(conversationId);
            var (spent, budget) = await _budget.GetMonthAsync();
            if (conversationId != CurrentConversationId)
                return;   // 查詢途中切到別的對話：那邊的載入會再查一次
            Usage.UpdateCost(conversation, spent, budget);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "讀取用量費用失敗");
        }
    }

    /// <summary>把目前的累計、上下文、模型與連線狀態推給 <see cref="Usage"/>。每次有相關變化都呼叫。</summary>
    private void RefreshUsage()
    {
        Usage.Update(new ConversationUsageSnapshot(
            HasData: _tally.HasData || LastUsage is not null,
            ContextUsed: ContextUsed,
            ContextLimit: ContextWindow,
            InputTokens: _tally.TotalInputIncludingCache,
            CacheReadTokens: _tally.TotalCacheRead,
            OutputTokens: _tally.TotalOutput,
            TurnCount: Turns,
            ModelName: ModelName,
            ConnectionName: WorkbenchModel?.Connection.Name ?? "",
            Connection: CurrentConnectionStatus));
    }

    /// <summary>從資料庫重新加總（載入對話時）。<paramref name="compactionUsage"/> 是上下文整理的摘要呼叫用量（M22）。</summary>
    private void LoadUsageTally(IEnumerable<ChatMessage> messages, TokenUsage? compactionUsage = null) =>
        _tally.Load(messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Usage).Append(compactionUsage is { } extra && extra != TokenUsage.Zero ? extra : null));

    /// <summary>
    /// 上下文整理的事件（M22）：摘要那次呼叫的用量算進累計（同一把金鑰的錢），但不當成「目前的上下文大小」。
    /// 整理的畫面（整理中提示、分隔線）不在這裡。
    /// </summary>
    private void OnCompactionEvent(ChatEvent chatEvent)
    {
        switch (chatEvent)
        {
            case CompactionStarted { Kind: CompactionKind.Summary }:
                _inCompaction = true;
                break;
            case CompactionCompleted completed:
                _inCompaction = false;
                if (completed.Usage is not null)
                {
                    _tally.Complete(completed.Usage);
                    RefreshUsage();
                }
                _ = RefreshCostAsync();   // 摘要呼叫的費用已經寫入（RefreshCostAsync 自己處理例外）
                break;
            case CompactionFailed:
                _inCompaction = false;
                _tally.Abandon();   // 失敗的那次沒有存檔，不計入（和失敗的回應一樣）
                RefreshUsage();
                break;
        }
    }

    /// <summary>一次請求完成（工具迴圈的中間步驟也算）：把它的 usage 併入已存的累計。</summary>
    private void CommitUsage(Completed completed)
    {
        _tally.Complete(completed.Message.Usage);
        RefreshUsage();
    }

    /// <summary>
    /// 失敗或中止的回應：以資料庫為準重新加總（它可能存了部分的 usage，也可能沒有）。
    /// 切到別的對話時不套用。
    /// </summary>
    private async Task ReloadUsageAsync(string conversationId)
    {
        var messages = await _store.GetMessagesAsync(conversationId);
        var compactionUsage = await _store.GetCompactionUsageAsync(conversationId);
        if (conversationId != CurrentConversationId)
            return;
        LoadUsageTally(messages, compactionUsage);
        RefreshUsage();
    }

    /// <summary>「重試」：就地測試目前模型的連線（用已儲存的金鑰），結果寫進 <see cref="ConnectionStatusCache"/>，和設定首頁的做法相同。</summary>
    private async Task RetryConnectionAsync()
    {
        if (_tester is null || _retryingConnection || WorkbenchModel is not { } model)
            return;

        _retryingConnection = true;
        try
        {
            var result = await _tester.TestAsync(model.Connection, null, CancellationToken.None);
            _status.Set(model.Connection.Id, result);
        }
        finally
        {
            _retryingConnection = false;
        }
    }
}
