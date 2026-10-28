namespace Lumi.App.Controls;

/// <summary>
/// 管理底下多個 <see cref="EmojiTextBlock"/> 的跨段落選取（例如 Markdown 回答）。
/// 裡面的文字區塊右鍵「複製」時，先問它有沒有跨段落的選取。
/// </summary>
public interface ITextSelectionScope
{
    /// <summary>目前選取的文字（跨段落時已用換行接起來）；沒有選取是空字串。</summary>
    string SelectedText { get; }
}
