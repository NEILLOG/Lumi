using Lumi.Core.Browser;
using Lumi.Core.Context;
using Lumi.Core.ContextNotes;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Chat;

/// <summary>
/// 刪除整個對話的唯一入口（工作台的刪除命令只呼叫它）。順序固定：
/// 刪資料庫（訊息、上下文項目隨外鍵一起刪）→ 研究本資料夾（M35，失敗不影響刪除）→ 清掉權限記憶 → 關閉這則對話的瀏覽器分頁（M18，失敗不影響刪除）→ 清附件檔案。
/// 前面失敗就不做後面：資料庫刪不掉時，權限與附件都不動；附件清理失敗時資料庫已經刪了，檔案會在下次啟動時清掉。
/// </summary>
public sealed class ConversationDeletionService(
    IConversationStore store, ToolPermissionMemory memory, AttachmentCleaner cleaner, IBrowserSessions? browser = null,
    IContextNoteStore? notes = null, IContextNoteFiles? files = null, ILogger<ConversationDeletionService>? logger = null)
{
    private readonly IBrowserSessions _browser = browser ?? new NullBrowserSessions();
    private readonly IContextNoteStore _notes = notes ?? new InMemoryContextNoteStore();
    private readonly IContextNoteFiles _files = files ?? new InMemoryContextNoteFiles();
    private readonly ILogger<ConversationDeletionService> _logger = logger ?? NullLogger<ConversationDeletionService>.Instance;

    /// <returns>對話存在而且已刪除回 true；不存在回 false（仍然會清權限與附件，兩者都沒有副作用）。</returns>
    public async Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        // 資料庫刪除會串連刪掉研究本的列，所以要先查出它的編號，之後才知道資料夾在哪
        var noteId = await FindNoteIdAsync(conversationId, ct).ConfigureAwait(false);
        var deleted = await store.DeleteAsync(conversationId, ct).ConfigureAwait(false);
        if (noteId is not null)
            await DeleteNoteFolderAsync(noteId, ct).ConfigureAwait(false);
        memory.Clear(conversationId);
        await CloseBrowserTabsAsync(conversationId, ct).ConfigureAwait(false);
        await cleaner.CleanAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// 清除對話內容（M22，小精靈專屬對話用）：清掉全部訊息與整理紀錄、權限記憶、瀏覽器分頁與不再被引用的附件，
    /// 但保留對話本身（id、標題）。順序同 <see cref="DeleteAsync"/>：資料庫清不掉就不動後面。
    /// </summary>
    public async Task ClearAsync(string conversationId, CancellationToken ct = default)
    {
        await store.ClearMessagesAsync(conversationId, ct).ConfigureAwait(false);
        memory.Clear(conversationId);
        await CloseBrowserTabsAsync(conversationId, ct).ConfigureAwait(false);
        await cleaner.CleanAsync(ct).ConfigureAwait(false);
    }

    /// <summary>查詢失敗就當成沒有研究本（只記 log，不影響刪除）。</summary>
    private async Task<string?> FindNoteIdAsync(string conversationId, CancellationToken ct)
    {
        try
        {
            return (await _notes.GetByConversationAsync(conversationId, ct).ConfigureAwait(false))?.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("查詢研究本失敗（{Type}），刪除對話時不清研究本資料夾", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>研究本資料夾刪不掉不能讓刪除對話失敗（資料庫已經刪了），只記 log（使用者取消除外）。</summary>
    private async Task DeleteNoteFolderAsync(string noteId, CancellationToken ct)
    {
        try
        {
            await _files.DeleteAsync(noteId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("刪除研究本資料夾失敗（{Type}）", ex.GetType().Name);
        }
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
