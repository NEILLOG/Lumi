using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.Resources;

namespace Lumi.App.Tests.Headless;

public class MarkdownRenderingTests
{
    private sealed class FakeServices
    {
        public List<Uri> Opened { get; } = [];
        public List<string> Copied { get; } = [];
        public MarkdownServices Services => new(
            uri => { Opened.Add(uri); return Task.CompletedTask; },
            text => { Copied.Add(text); return Task.CompletedTask; });
    }

    private static (MarkdownView View, Window Window, FakeServices Fake) Show(string markdown, bool streaming = false, double width = 640)
    {
        var fake = new FakeServices();
        var view = new MarkdownView { Services = fake.Services, IsStreaming = streaming, Markdown = markdown };
        var window = new Window { Width = width, Height = 800, Content = view };
        window.Show();
        view.Flush();
        window.UpdateLayout();
        return (view, window, fake);
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (var child in root.GetLogicalDescendants().OfType<Control>())
            yield return child;
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control => Descendants(root).OfType<T>();

    private static string RunsText(MarkdownTextBlock block) =>
        string.Concat(block.Inlines!.OfType<Run>().Select(r => r.Text));

    // ---- 結構 ----

    [AvaloniaFact]
    public void Headings_GetLevelClasses_AndParagraphsRenderAsMarkdownText()
    {
        var (view, _, _) = Show("# 大標\n\n## 次標\n\n一般段落");

        Assert.Equal(3, view.RenderedBlocks.Count);
        Assert.Contains("md-h1", view.RenderedBlocks[0].Classes);
        Assert.Contains("md-h2", view.RenderedBlocks[1].Classes);
        var paragraph = Assert.IsType<MarkdownTextBlock>(view.RenderedBlocks[2]);
        Assert.Equal("一般段落", RunsText(paragraph));
    }

    [AvaloniaFact]
    public void InlineStyles_BecomeRunProperties_AndClasses()
    {
        var (view, _, _) = Show("普通 **粗** *斜* ~~刪~~ `code`");

        var runs = ((MarkdownTextBlock)view.RenderedBlocks[0]).Inlines!.OfType<Run>().ToList();
        Assert.Equal(FontWeight.Bold, runs.Single(r => r.Text == "粗").FontWeight);
        Assert.Equal(FontStyle.Italic, runs.Single(r => r.Text == "斜").FontStyle);
        Assert.NotNull(runs.Single(r => r.Text == "刪").TextDecorations);
        Assert.Contains("md-code", runs.Single(r => r.Text == "code").Classes);
    }

    [AvaloniaFact]
    public void Emoji_InsideBoldAndCodeAndTables_StayWholeAndUseEmojiFont()
    {
        const string flame = "❤️‍🔥";
        var (view, _, _) = Show($"**{flame}粗**\n\n```\ncode {flame}\n```\n\n| a |\n|---|\n| {flame} |");

        var bold = ((MarkdownTextBlock)view.RenderedBlocks[0]).Inlines!.OfType<Run>().First(r => r.Text == flame);
        Assert.Equal(EmojiText.EmojiFont, bold.FontFamily);
        Assert.Equal(FontWeight.Bold, bold.FontWeight);   // 樣式與 emoji 字型並存

        var codeText = All<EmojiTextBlock>(view.RenderedBlocks[1]).Single(b => b.Classes.Contains("md-codetext"));
        Assert.Contains(codeText.Inlines!.OfType<Run>(), r => r.Text == flame);

        var cell = All<MarkdownTextBlock>(view.RenderedBlocks[2]).Last();
        Assert.Contains(cell.Inlines!.OfType<Run>(), r => r.Text == flame);
    }

    [AvaloniaFact]
    public void Lists_RenderMarkers_AndNestedLists()
    {
        var (view, _, _) = Show("1. 一\n2. 二\n   - 內層\n\n- 無序");

        var markers = All<TextBlock>(view).Where(t => t is not MarkdownTextBlock).Select(t => t.Text).ToList();
        Assert.Contains("1.", markers);
        Assert.Contains("2.", markers);
        Assert.Contains("◦", markers);   // 巢狀的第二層用空心圓
        Assert.Contains("•", markers);
    }

    [AvaloniaFact]
    public void OrderedList_StartsAtItsOwnNumber()
    {
        var (view, _, _) = Show("5. 五\n6. 六");

        var markers = All<TextBlock>(view).Where(t => t is not MarkdownTextBlock).Select(t => t.Text).ToList();
        Assert.Equal(["5.", "6."], markers);
    }

    [AvaloniaFact]
    public void Quote_And_Rule()
    {
        var (view, _, _) = Show("> 引用\n\n---\n\n後面");

        Assert.Contains("md-quote", view.RenderedBlocks[0].Classes);
        Assert.Contains("md-rule", view.RenderedBlocks[1].Classes);
    }

    [AvaloniaFact]
    public void Table_HasHeaderCells_AndAlignment()
    {
        var (view, _, _) = Show("| 名稱 | 數量 |\n|:---|---:|\n| 蘋果 | 3 |\n| 梨子 | 12 |");

        var grid = All<Grid>(view).Single(g => g.ColumnDefinitions.Count == 2);
        Assert.Equal(3, grid.RowDefinitions.Count);
        var cells = grid.Children.OfType<Border>().ToList();
        Assert.Equal(6, cells.Count);
        Assert.Equal(2, cells.Count(c => c.Classes.Contains("md-thead")));
        var rightCell = (MarkdownTextBlock)cells.Single(c => Grid.GetRow(c) == 1 && Grid.GetColumn(c) == 1).Child!;
        Assert.Equal(TextAlignment.Right, rightCell.TextAlignment);
    }

    // ---- 程式碼區塊 ----

    [AvaloniaFact]
    public void CodeBlock_ShowsLanguage_AndCopyButtonCopiesOnlyTheCode()
    {
        var (view, _, fake) = Show("```csharp\nvar x = 1;\n```");

        var block = view.RenderedBlocks[0];
        Assert.Contains("md-codeblock", block.Classes);
        Assert.Equal("csharp", All<TextBlock>(block).Single(t => t.Classes.Contains("md-codelang")).Text);
        var button = All<Button>(block).Single();
        Assert.Equal(Strings.Workbench_Copy, button.Content);

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        Assert.Equal(["var x = 1;"], fake.Copied);
        Assert.Equal(Strings.Workbench_Copied, button.Content);
    }

    [AvaloniaFact]
    public void CodeBlock_WithoutLanguage_HasEmptyLabel_AndDoesNotWrap()
    {
        var (view, _, _) = Show("```\n一行很長很長很長很長很長很長的程式碼\n```");

        var block = view.RenderedBlocks[0];
        Assert.Equal("", All<TextBlock>(block).Single(t => t.Classes.Contains("md-codelang")).Text);
        Assert.Equal(TextWrapping.NoWrap, All<EmojiTextBlock>(block).Single(t => t.Classes.Contains("md-codetext")).TextWrapping);
    }

    // ---- 串流增量渲染 ----

    [AvaloniaFact]
    public void Streaming_OnlyRebuildsFromTheFirstChangedBlock()
    {
        var (view, _, _) = Show("# 標題\n\n第一段\n\n第二段寫到一半", streaming: true);
        var first = view.RenderedBlocks[0];
        var second = view.RenderedBlocks[1];
        var third = view.RenderedBlocks[2];

        view.Markdown = "# 標題\n\n第一段\n\n第二段寫到一半又多了一些";
        view.Flush();

        Assert.Same(first, view.RenderedBlocks[0]);       // 沒變的沿用同一個控制項
        Assert.Same(second, view.RenderedBlocks[1]);
        Assert.NotSame(third, view.RenderedBlocks[2]);    // 最後一個在變，被重建
        Assert.Equal("第二段寫到一半又多了一些", RunsText((MarkdownTextBlock)view.RenderedBlocks[2]));
    }

    [AvaloniaFact]
    public void Streaming_NewBlocksAreAppended_ExistingOnesKept()
    {
        var (view, _, _) = Show("第一段", streaming: true);
        var first = view.RenderedBlocks[0];

        view.Markdown = "第一段\n\n第二段";
        view.Flush();

        Assert.Equal(2, view.RenderedBlocks.Count);
        Assert.Same(first, view.RenderedBlocks[0]);
    }

    [AvaloniaFact]
    public void Streaming_UnclosedFenceBecomesCodeBlock_ThenKeepsGrowing()
    {
        var (view, _, _) = Show("前言\n\n```python\nprint(1)", streaming: true);
        Assert.Contains("md-codeblock", view.RenderedBlocks[1].Classes);

        view.Markdown = "前言\n\n```python\nprint(1)\nprint(2)\n```\n\n結尾";
        view.Flush();

        Assert.Equal(3, view.RenderedBlocks.Count);
        Assert.Equal("結尾", RunsText((MarkdownTextBlock)view.RenderedBlocks[2]));
    }

    [AvaloniaFact]
    public void Streaming_ManyUpdatesBeforeFlush_RenderOnlyTheLatest()
    {
        var (view, _, _) = Show("", streaming: true);

        for (var i = 1; i <= 50; i++)
            view.Markdown = new string('字', i);   // 排程中，不會每次都渲染
        view.Flush();

        Assert.Equal(50, RunsText((MarkdownTextBlock)Assert.Single(view.RenderedBlocks)).Length);
    }

    [AvaloniaFact]
    public void WhenStreamingEnds_FinalTextIsRenderedImmediately()
    {
        var (view, _, _) = Show("", streaming: true);

        view.Markdown = "# 完成";
        view.IsStreaming = false;   // 不需要 Flush：回應結束就立刻渲染

        Assert.Contains("md-h1", view.RenderedBlocks[0].Classes);
    }

    [AvaloniaFact]
    public void ShrinkingOrClearing_RemovesOldBlocks()
    {
        var (view, _, _) = Show("一\n\n二\n\n三");

        view.Markdown = "一";
        view.Flush();
        Assert.Single(view.RenderedBlocks);

        view.Markdown = "";
        view.Flush();
        Assert.Empty(view.RenderedBlocks);
    }

    // ---- 連結 ----

    private static Point CenterOfText(MarkdownTextBlock block, int position)
    {
        var rect = block.TextLayout.HitTestTextPosition(position);
        return new Point(rect.X + 1, rect.Y + rect.Height / 2);
    }

    [AvaloniaFact]
    public void Links_RecordTextRanges_OnlyForAllowedSchemes()
    {
        var (view, _, _) = Show("看 [官網](https://example.com) 和 [壞的](javascript:alert(1)) 吧");

        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var link = Assert.Single(block.Links);
        Assert.Equal(new Uri("https://example.com"), link.Uri);
        Assert.Equal("看 官網 和 壞的 吧".IndexOf("官網", StringComparison.Ordinal), link.Start);
        Assert.Equal(2, link.Length);
        // 不在白名單的連結顯示成純文字
        Assert.DoesNotContain(block.Inlines!.OfType<Run>(), r => r.Text == "壞的" && r.Classes.Contains("md-link"));
        Assert.Contains(block.Inlines!.OfType<Run>(), r => r.Text == "官網" && r.Classes.Contains("md-link"));
    }

    [AvaloniaFact]
    public void Link_HitTest_FindsTheLink_AndNotTheTextAround()
    {
        var (view, _, _) = Show("前面的文字 [點這裡](https://example.com/a) 後面的文字");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var link = Assert.Single(block.Links);

        Assert.Equal(link, block.LinkAt(CenterOfText(block, link.Start)));
        Assert.Equal(link, block.LinkAt(CenterOfText(block, link.Start + link.Length - 1)));
        Assert.Null(block.LinkAt(CenterOfText(block, 0)));
        Assert.Null(block.LinkAt(CenterOfText(block, link.Start + link.Length + 2)));
    }

    [AvaloniaFact]
    public void Link_AfterLineBreakAndEmoji_RangeStillMatchesTheLayout()
    {
        // 連結前面有換行與組合 emoji：文字範圍的換算要和 TextLayout 的位置一致
        var (view, _, _) = Show("第一行\n第二行 ❤️‍🔥 然後 [連結文字](https://example.com/x) 結尾");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var link = Assert.Single(block.Links);

        var hit = block.LinkAt(CenterOfText(block, link.Start + 1));

        Assert.Equal(link, hit);
        Assert.Null(block.LinkAt(CenterOfText(block, link.Start - 3)));
    }

    [AvaloniaFact]
    public void ClickingALink_OpensItThroughTheLauncher_ClickingElsewhereDoesNot()
    {
        var (view, window, fake) = Show("前面的文字 [點這裡](https://example.com/go) 後面的文字");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var link = Assert.Single(block.Links);

        // 把滑鼠點擊送進視窗：座標是相對於視窗的
        var onLink = block.TranslatePoint(CenterOfText(block, link.Start + 1), window)!.Value;
        window.MouseDown(onLink, MouseButton.Left);
        window.MouseUp(onLink, MouseButton.Left);
        Assert.Equal([new Uri("https://example.com/go")], fake.Opened);

        var elsewhere = block.TranslatePoint(CenterOfText(block, 0), window)!.Value;
        window.MouseDown(elsewhere, MouseButton.Left);
        window.MouseUp(elsewhere, MouseButton.Left);
        Assert.Single(fake.Opened);
    }
}
