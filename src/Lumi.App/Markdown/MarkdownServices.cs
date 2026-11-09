using Avalonia;
using Avalonia.Controls;
using Lumi.Core.Diagrams;

namespace Lumi.App.Markdown;

/// <summary>
/// 渲染器需要的外部動作：開啟連結、複製文字、把 Mermaid 畫成圖（<paramref name="Diagrams"/>，null＝顯示程式碼）、
/// 用看圖視窗放大一張圖（<paramref name="OpenImage"/>）。測試用假的，正式用 <see cref="MarkdownView"/> 提供的實作。
/// </summary>
public sealed record MarkdownServices(
    Func<Uri, Task> OpenLink,
    Func<string, Task> CopyText,
    IDiagramRenderer? Diagrams = null,
    Action<Control, Size>? OpenImage = null);

/// <summary>畫流程圖的服務（App 啟動時從 DI 設定）；<see cref="MarkdownView"/> 是在 XAML 裡建立的，拿不到 DI。</summary>
public static class MarkdownDiagrams
{
    public static IDiagramRenderer? Renderer { get; set; }
}
