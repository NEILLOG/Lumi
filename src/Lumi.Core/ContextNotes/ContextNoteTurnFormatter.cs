using Lumi.Core.Chat;

namespace Lumi.Core.ContextNotes;

/// <param name="Token">對話文字裡的「圖N」。</param>
/// <param name="AttachmentFileName">附件資料夾裡的檔名；和 <paramref name="FilePath"/> 二選一。</param>
public sealed record TurnImage(string Token, string? AttachmentFileName, string? FilePath, string MediaType, string DisplayName);

/// <param name="LastUserExcerpt">這批最後一則使用者訊息的文字去掉換行後的前 30 字。</param>
public sealed record TurnText(string Text, IReadOnlyList<TurnImage> Images, string? LastUserExcerpt);

/// <summary>把要整理的訊息轉成給書記看的對話文字（計畫 4.6）。</summary>
public static class ContextNoteTurnFormatter
{
    // TODO(35C)
    /// <summary>要整理的訊息範圍（4.6）。</summary>
    public static IReadOnlyList<ChatMessage> SelectPending(IReadOnlyList<ChatMessage> messages, int uptoSeq, string? uptoMessageId) =>
        throw new NotImplementedException();

    // TODO(35C)
    public static TurnText Format(IReadOnlyList<ChatMessage> messages, int? maxTotalChars = null) => throw new NotImplementedException();
}
