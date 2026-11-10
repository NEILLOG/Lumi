using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Lumi.App.Markdown;

namespace Lumi.App.Tests.Headless;

/// <summary>選取回答的文字再複製：拿到的是 Markdown（<see cref="MarkdownView.SelectedMarkdown"/>）。</summary>
public class MarkdownSelectionCopyTests
{
    private static (MarkdownView View, Window Window) Show(string markdown)
    {
        var view = new MarkdownView { Services = new MarkdownServices(_ => Task.CompletedTask, _ => Task.CompletedTask), Markdown = markdown };
        var window = new Window { Width = 640, Height = 1600, Content = view };
        window.Show();
        view.Flush();
        window.UpdateLayout();
        return (view, window);
    }

    private static List<SelectableTextBlock> TextBlocks(MarkdownView view) =>
        view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();

    private static void SelectAll(MarkdownView view)
    {
        foreach (var block in TextBlocks(view))
            block.SelectAll();
    }

    [AvaloniaFact]
    public void SelectingEverything_GivesBackTheMarkdown()
    {
        const string markdown = """
            ## 標題

            一段有**粗體**、*斜體*、~~刪除~~、`code` 和[連結](https://example.com)的文字。

            - 第一項
            - 第二項
              - 子項目

            1. 一
            2. 二

            > 引用的第一段
            >
            > 引用的第二段

            ```csharp
            var x = 1;
            ```

            | 欄一 | 欄二 |
            | --- | --- |
            | 甲 | 乙 |

            最後一段
            """;
        var (view, _) = Show(markdown);

        SelectAll(view);

        Assert.Equal(markdown, view.SelectedMarkdown);
    }

    [AvaloniaFact]
    public void SelectionInsideOneBlock_KeepsOnlyInlineFormat()
    {
        var (view, _) = Show("## 前面**粗體**後面");
        var heading = (MarkdownTextBlock)view.RenderedBlocks[0];

        heading.SelectionStart = 1;
        heading.SelectionEnd = 4;

        Assert.Equal("面**粗體**", view.SelectedMarkdown);   // 沒有「## 」
    }

    [AvaloniaFact]
    public void SelectionInsideOneCodeBlock_IsTheRawCode()
    {
        var (view, _) = Show("```\nvar **x** = 1;\n```");
        var code = TextBlocks(view).Single();

        code.SelectAll();

        Assert.Equal("var **x** = 1;", view.SelectedMarkdown);
    }

    [AvaloniaFact]
    public async Task CtrlCInsideAFocusedBlock_CopiesMarkdownToo()
    {
        var (view, window) = Show("前面**粗體**後面");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        block.SelectAll();
        block.Focus();

        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
        await Task.Delay(50);

        Assert.Equal("前面**粗體**後面", await window.Clipboard!.TryGetTextAsync());
    }
}
