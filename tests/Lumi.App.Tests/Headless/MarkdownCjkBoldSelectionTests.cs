using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Markdown;

namespace Lumi.App.Tests.Headless;

/// <summary>FX-12：中文粗體的回答全選再複製，還原的 Markdown 和原文一樣。</summary>
public class MarkdownCjkBoldSelectionTests
{
    /// <summary>單一段落：全選還原成同樣的字串。</summary>
    [AvaloniaFact]
    public void SelectingAll_GivesBackTheSameMarkdown()
    {
        AssertRoundTrip("**重點：**內容");
    }

    /// <summary>
    /// 清單：多個區塊全選才會還原清單符號（只選一個區塊時和既有行為一樣只留行內格式）。
    /// </summary>
    [AvaloniaFact]
    public void SelectingAllItems_GivesBackTheListMarkers()
    {
        AssertRoundTrip("- **測試：**123\n- 第二項\n\n1. **步驟一：**打開設定\n2. 第二步");
    }

    private static void AssertRoundTrip(string markdown)
    {
        var view = new MarkdownView { Services = new MarkdownServices(_ => Task.CompletedTask, _ => Task.CompletedTask), Markdown = markdown };
        var window = new Window { Width = 640, Height = 800, Content = view };
        window.Show();
        view.Flush();
        window.UpdateLayout();

        foreach (var block in view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible))
            block.SelectAll();

        Assert.Equal(markdown, view.SelectedMarkdown);
    }
}
