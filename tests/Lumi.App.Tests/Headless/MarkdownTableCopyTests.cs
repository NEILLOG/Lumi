using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumi.App.Markdown;
using Lumi.App.Resources;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// 表格的複製：選取複製時空的儲存格要保留欄位（FX-16）；表格上方有「複製表格」鈕（FT-07）。
/// </summary>
public class MarkdownTableCopyTests
{
    private static (MarkdownView View, Window Window) Show(string markdown, MarkdownServices? services = null)
    {
        var view = new MarkdownView { Services = services ?? new MarkdownServices(_ => Task.CompletedTask, _ => Task.CompletedTask), Markdown = markdown };
        var window = new Window { Width = 640, Height = 1600, Content = view };
        window.Show();
        view.Flush();
        window.UpdateLayout();
        return (view, window);
    }

    private static void SelectAll(MarkdownView view)
    {
        foreach (var block in view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible))
            block.SelectAll();
    }

    /// <summary>每一列的欄數（沒有跳脫的 <c>|</c> 時：竪線數減一）。</summary>
    private static int[] ColumnCounts(string markdown) =>
        markdown.Split('\n').Select(line => line.Count(c => c == '|') - 1).ToArray();

    [AvaloniaTheory]
    [InlineData("| A | B | C |\n| --- | --- | --- |\n| 1 |  | 3 |\n| 4 |  | 6 |")]   // 中間一欄全空
    [InlineData("| A | B | C |\n| --- | --- | --- |\n| 1 | 2 |  |\n| 4 | 5 |  |")]   // 最後一欄全空
    [InlineData("| A | B | C |\n| --- | --- | --- |\n|  | 2 | 3 |\n|  | 5 | 6 |")]   // 第一欄全空
    [InlineData("| A | B | C |\n| --- | --- | --- |\n|  | 2 |  |\n| 4 |  | 6 |")]   // 某幾格空
    public void SelectingAll_KeepsEveryColumn(string table)
    {
        var (view, _) = Show(table);

        SelectAll(view);

        var copied = view.SelectedMarkdown;
        Assert.Equal(table, copied);
        Assert.All(ColumnCounts(copied), count => Assert.Equal(3, count));
    }

    [AvaloniaFact]
    public void SelectingOnlyTheMiddleColumns_KeepsTheirRange()
    {
        var (view, _) = Show("| A | B | C |\n| --- | --- | --- |\n| 1 |  | 3 |");
        foreach (var block in view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible))
        {
            if (MarkdownCopy.GetInfo(block) is { Kind: MarkdownCopyKind.TableCell, Column: 1 or 2 })
                block.SelectAll();
        }

        // 只選到 B、C 欄：B 欄是空的，仍要占一欄
        Assert.Equal("| B | C |\n| --- | --- |\n|  | 3 |", view.SelectedMarkdown);
    }

    [AvaloniaFact]
    public void TableHasACopyButtonAboveIt_AndItCopiesTheTable()
    {
        const string table = "| A | B |\n| --- | --- |\n| 1 |  |";
        string? copiedMarkdown = null, copiedText = null;
        var services = new MarkdownServices(_ => Task.CompletedTask, _ => Task.CompletedTask, null, null,
            (markdown, text) => { copiedMarkdown = markdown; copiedText = text; return Task.CompletedTask; });
        var (view, _) = Show(table, services);

        var button = view.GetVisualDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == Strings.Markdown_CopyTableTip);
        var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Content is Border { Classes: var c } && c.Contains("md-table"));

        Assert.Equal(Strings.Markdown_CopyTable, ((TextBlock)((StackPanel)button.Content!).Children[1]).Text);
        Assert.True(button.TranslatePoint(default, view)!.Value.Y < scroller.TranslatePoint(default, view)!.Value.Y, "複製鈕要在表格上方");
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, button.HorizontalAlignment);

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(table, copiedMarkdown);
        Assert.Equal("A\tB\n1\t", copiedText);
    }
}
