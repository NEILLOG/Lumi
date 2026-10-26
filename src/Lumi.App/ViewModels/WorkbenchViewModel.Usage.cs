using Lumi.Core.Chat;
using Lumi.Core.Models;

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

    /// <summary>本對話用量（頂端用量圖示與面板綁定這裡）。</summary>
    public ConversationUsageViewModel Usage { get; }

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

    /// <summary>從資料庫重新加總（載入對話時）。</summary>
    private void LoadUsageTally(IEnumerable<ChatMessage> messages) =>
        _tally.Load(messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Usage));

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
        if (conversationId != CurrentConversationId)
            return;
        LoadUsageTally(messages);
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
