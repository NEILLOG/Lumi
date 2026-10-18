namespace Lumi.App.Markdown;

/// <summary>渲染器需要的外部動作：開啟連結、複製文字。測試用假的，正式用 <see cref="MarkdownView"/> 提供的實作。</summary>
public sealed record MarkdownServices(Func<Uri, Task> OpenLink, Func<string, Task> CopyText);
