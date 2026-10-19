using Microsoft.Extensions.Logging;

namespace Lumi.Core.Context;

/// <summary>
/// 清掉沒有任何訊息引用的附件檔案（例如圖片存了、訊息卻沒存成功，或對話被刪除之後）。
/// 只能在沒有送出進行中的時候做（啟動時）：圖片先存檔、訊息後存，中間的檔案看起來像孤兒。
/// </summary>
public sealed class AttachmentCleaner(IAttachmentStore store, IAttachmentReferences references, ILogger<AttachmentCleaner> logger)
{
    public async Task<int> CleanAsync(CancellationToken ct = default)
    {
        var referenced = await references.GetReferencedFileNamesAsync(ct).ConfigureAwait(false);
        var deleted = await store.DeleteUnreferencedAsync(referenced, ct).ConfigureAwait(false);
        if (deleted > 0)
            logger.LogInformation("清掉 {Count} 個沒有被引用的附件檔案", deleted);
        return deleted;
    }
}
