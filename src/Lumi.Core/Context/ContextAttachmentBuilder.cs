using Lumi.Core.Chat;

namespace Lumi.Core.Context;

/// <summary>要嵌進使用者訊息的區塊，以及已經處理掉的項目（不管成功或有說明地失敗，都不再重複送）。</summary>
public sealed record ContextAttachments(IReadOnlyList<ContentBlock> Blocks, IReadOnlyList<string> ItemIds)
{
    public static readonly ContextAttachments None = new([], []);
}

/// <summary>
/// 把「還沒送出」的上下文項目變成使用者訊息的內容區塊（檔案文字、資料夾檔案樹、圖片）。
/// 要讀檔案，所以實作在 Lumi.Tools。讀不到的項目不丟例外：產生一個說明原因的區塊。
/// </summary>
public interface IContextAttachmentBuilder
{
    Task<ContextAttachments> BuildAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default);

    /// <summary>送出前的 token 預估（不存圖片、不標記項目）。</summary>
    Task<int> EstimateTokensAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default);
}

public sealed class NullContextAttachmentBuilder : IContextAttachmentBuilder
{
    public Task<ContextAttachments> BuildAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default) =>
        Task.FromResult(ContextAttachments.None);

    public Task<int> EstimateTokensAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default) => Task.FromResult(0);
}
