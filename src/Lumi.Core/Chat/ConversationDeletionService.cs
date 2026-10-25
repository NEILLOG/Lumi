using Lumi.Core.Browser;
using Lumi.Core.Context;
using Lumi.Core.Tools;

namespace Lumi.Core.Chat;

/// <summary>
/// 刪除整個對話的唯一入口（工作台的刪除命令只呼叫它）。順序固定：
/// 刪資料庫（訊息、上下文項目隨外鍵一起刪）→ 清掉權限記憶 → 關閉這則對話的瀏覽器分頁（M18，失敗不影響刪除）→ 清附件檔案。
/// 前面失敗就不做後面：資料庫刪不掉時，權限與附件都不動；附件清理失敗時資料庫已經刪了，檔案會在下次啟動時清掉。
/// </summary>
public sealed class ConversationDeletionService(
    IConversationStore store, ToolPermissionMemory memory, AttachmentCleaner cleaner, IBrowserSessions? browser = null)
{
    private readonly IBrowserSessions _browser = browser ?? new NullBrowserSessions();

    /// <returns>對話存在而且已刪除回 true；不存在回 false（仍然會清權限與附件，兩者都沒有副作用）。</returns>
    public async Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        var deleted = await store.DeleteAsync(conversationId, ct).ConfigureAwait(false);
        memory.Clear(conversationId);
        await CloseBrowserTabsAsync(conversationId, ct).ConfigureAwait(false);
        await cleaner.CleanAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>關閉瀏覽器失敗不能讓刪除對話失敗，所以例外一律吞掉（使用者取消除外）。</summary>
    private async Task CloseBrowserTabsAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            await _browser.CloseAsync(conversationId, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
    }
}
