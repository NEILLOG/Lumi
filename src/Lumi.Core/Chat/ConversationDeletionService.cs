using Lumi.Core.Branches;
using Lumi.Core.Browser;
using Lumi.Core.Context;
using Lumi.Core.ContextNotes;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Chat;

/// <summary>
/// 刪除整個對話的唯一入口（工作台的刪除命令只呼叫它）。順序固定：
/// 先由深到淺把底下的支線（最多兩層，M36 4.9）各自做一次下面的步驟，最後才是對話自己；附件清理只在最後做一次。
/// 每一則的步驟：刪資料庫（訊息、上下文項目隨外鍵一起刪）→ 研究本資料夾（M35，失敗不影響刪除）→ 清掉權限記憶 → 關閉這則對話的瀏覽器分頁（M18，失敗不影響刪除）。
/// 前面失敗就不做後面：資料庫刪不掉時，權限與附件都不動；附件清理失敗時資料庫已經刪了，檔案會在下次啟動時清掉。
/// </summary>
public sealed class ConversationDeletionService(
    IConversationStore store, ToolPermissionMemory memory, AttachmentCleaner cleaner, IBrowserSessions? browser = null,
    IContextNoteStore? notes = null, IContextNoteFiles? files = null, ILogger<ConversationDeletionService>? logger = null,
    IBranchStore? branches = null)
{
    private readonly IBrowserSessions _browser = browser ?? new NullBrowserSessions();
    private readonly IContextNoteStore _notes = notes ?? new InMemoryContextNoteStore();
    private readonly IContextNoteFiles _files = files ?? new InMemoryContextNoteFiles();
    private readonly ILogger<ConversationDeletionService> _logger = logger ?? NullLogger<ConversationDeletionService>.Instance;
    private readonly IBranchStore _branches = branches ?? new InMemoryBranchStore();

    /// <returns>對話存在而且已刪除回 true；不存在回 false（仍然會清權限與附件，兩者都沒有副作用）。</returns>
    public async Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        // 支線（M36 4.9）：刪上一層不會刪掉支線的對話本身，所以先由深到淺自己刪；支線的列隨它的對話一起刪
        foreach (var id in await DescendantsDeepestFirstAsync(conversationId, ct).ConfigureAwait(false))
            await DeleteOneAsync(id, ct).ConfigureAwait(false);

        var deleted = await DeleteOneAsync(conversationId, ct).ConfigureAwait(false);
        await cleaner.CleanAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>刪除一則對話（不含附件清理）：資料庫、研究本資料夾、權限記憶、瀏覽器分頁。</summary>
    private async Task<bool> DeleteOneAsync(string conversationId, CancellationToken ct)
    {
        // 資料庫刪除會串連刪掉研究本的列，所以要先查出它的編號，之後才知道資料夾在哪
        var noteId = await FindNoteIdAsync(conversationId, ct).ConfigureAwait(false);
        var deleted = await store.DeleteAsync(conversationId, ct).ConfigureAwait(false);
        if (noteId is not null)
            await DeleteNoteFolderAsync(noteId, ct).ConfigureAwait(false);
        memory.Clear(conversationId);
        await CloseBrowserTabsAsync(conversationId, ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>這則對話底下的全部支線（兩層以內），最深的排在前面；沒有支線時是空的。</summary>
    private async Task<List<string>> DescendantsDeepestFirstAsync(string conversationId, CancellationToken ct)
    {
        var result = new List<string>();
        await CollectDescendantsAsync(conversationId, 0, result, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>後序遞迴：先收孩子的孩子，再收孩子（最深的先進結果）。層數上限擋掉資料異常時的無限遞迴。</summary>
    private async Task CollectDescendantsAsync(string parentId, int level, List<string> result, CancellationToken ct)
    {
        if (level >= BranchLimits.MaxDepth)
            return;
        foreach (var child in await _branches.ListChildrenAsync(parentId, ct).ConfigureAwait(false))
        {
            await CollectDescendantsAsync(child.ConversationId, level + 1, result, ct).ConfigureAwait(false);
            result.Add(child.ConversationId);
        }
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
