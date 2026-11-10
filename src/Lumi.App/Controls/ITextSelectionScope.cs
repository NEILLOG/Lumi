namespace Lumi.App.Controls;

/// <summary>
/// 管理底下多個 <see cref="EmojiTextBlock"/> 的跨段落選取（例如 Markdown 回答）。
/// 裡面的文字區塊右鍵「複製」或按 Ctrl/Cmd+C 時，先交給它複製選取的範圍。
/// </summary>
public interface ITextSelectionScope
{
    /// <summary>把選取的範圍放進剪貼簿（Markdown 回答放 Markdown 加 HTML）；沒有選取回 false。</summary>
    Task<bool> CopySelectionAsync();
}
