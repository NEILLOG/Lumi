namespace Lumi.App.ViewModels;

/// <summary>寫入系統剪貼簿。ViewModel 不碰 Avalonia，測試用假的。</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);

    /// <summary>放 Markdown 原文（純文字）加轉好的 HTML：貼到 Word、Outlook 保留格式，貼到純文字的地方是原文。</summary>
    Task SetMarkdownAsync(string markdown);
}
