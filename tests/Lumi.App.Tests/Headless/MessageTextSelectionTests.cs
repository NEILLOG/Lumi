using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.Resources;

namespace Lumi.App.Tests.Headless;

/// <summary>22E：訊息文字可選取（拖曳選取、Ctrl+C、右鍵複製），同時點連結與捲動不受影響。</summary>
public class MessageTextSelectionTests
{
    private sealed class FakeServices
    {
        public List<Uri> Opened { get; } = [];
        public MarkdownServices Services => new(
            uri => { Opened.Add(uri); return Task.CompletedTask; },
            _ => Task.CompletedTask);
    }

    private static (MarkdownView View, Window Window, FakeServices Fake) Show(string markdown, double height = 800)
    {
        var fake = new FakeServices();
        var view = new MarkdownView { Services = fake.Services, Markdown = markdown };
        var window = new Window { Width = 640, Height = height, Content = view };
        window.Show();
        view.Flush();
        window.UpdateLayout();
        return (view, window, fake);
    }

    /// <summary>文字位置的中心點，換算成視窗座標（滑鼠事件用的座標）。</summary>
    private static Point CenterOfText(Window window, MarkdownTextBlock block, int position)
    {
        var rect = block.TextLayout.HitTestTextPosition(position);
        return block.TranslatePoint(new Point(rect.X + 1, rect.Y + rect.Height / 2), window)!.Value;
    }

    /// <summary>按住左鍵從 from 拖到 to 再放開（headless 的移動要帶著按鍵狀態，否則選取不會更新）。</summary>
    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
    }

    private static async Task<string?> ClipboardText(Window window)
    {
        return await window.Clipboard!.TryGetTextAsync();
    }

    [AvaloniaFact]
    public void DraggingOverParagraphText_SelectsIt()
    {
        var (view, window, _) = Show("一段可以選取的文字");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];

        Drag(window, CenterOfText(window, block, 0), CenterOfText(window, block, 4));

        Assert.True(block.SelectionEnd > block.SelectionStart);
        Assert.False(string.IsNullOrEmpty(block.SelectedText));
    }

    [AvaloniaFact]
    public async Task CtrlC_CopiesTheSelection()
    {
        var (view, window, _) = Show("複製這一段");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        Drag(window, CenterOfText(window, block, 0), CenterOfText(window, block, 2));
        block.Focus();

        window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);

        Assert.False(string.IsNullOrEmpty(block.SelectedText));
        Assert.Equal(block.SelectedText, await ClipboardText(window));
    }

    [AvaloniaFact]
    public async Task ContextMenuCopy_CopiesTheSelection_OrTheWholeTextWhenNothingIsSelected()
    {
        var (view, window, _) = Show("整段文字");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var copy = Assert.IsType<MenuItem>(Assert.Single(block.ContextMenu!.Items.OfType<MenuItem>()));
        Assert.Equal(Strings.Workbench_Copy, copy.Header);

        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(50);
        Assert.Equal("整段文字", await ClipboardText(window));

        Drag(window, CenterOfText(window, block, 0), CenterOfText(window, block, 1));
        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Task.Delay(50);
        Assert.Equal(block.SelectedText, await ClipboardText(window));
    }

    [AvaloniaFact]
    public void DraggingFromALink_IsASelection_NotAClick()
    {
        var (view, window, fake) = Show("前面 [點這裡](https://example.com/drag) 後面");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var link = Assert.Single(block.Links);

        var from = CenterOfText(window, block, link.Start + 1);
        Drag(window, from, new Point(from.X + 60, from.Y));

        Assert.Empty(fake.Opened);
    }

    [AvaloniaFact]
    public void ClickingALink_StillOpensIt_WhenTheTextIsSelectable()
    {
        var (view, window, fake) = Show("前面 [點這裡](https://example.com/click) 後面");
        var block = (MarkdownTextBlock)view.RenderedBlocks[0];
        var link = Assert.Single(block.Links);

        var at = CenterOfText(window, block, link.Start + 1);
        Drag(window, at, at);

        Assert.Equal([new Uri("https://example.com/click")], fake.Opened);
    }

    [AvaloniaFact]
    public void DraggingToSelect_DoesNotScrollTheConversation()
    {
        var paragraphs = string.Join("\n\n", Enumerable.Range(1, 40).Select(i => $"第 {i} 段的內容"));
        var (view, window, _) = Show(paragraphs, height: 400);
        window.Content = null;
        var scroller = new ScrollViewer { Content = view };
        window.Content = scroller;
        window.UpdateLayout();
        scroller.Offset = new Vector(0, 60);
        window.UpdateLayout();
        var block = (MarkdownTextBlock)view.RenderedBlocks[3];
        var before = scroller.Offset;

        var start = CenterOfText(window, block, 0);
        Drag(window, start, new Point(start.X + 20, start.Y + 150));

        Assert.Equal(before, scroller.Offset);
    }

    [AvaloniaFact]
    public void UserAndThinkingText_AreSelectable_ButNotTabStops()
    {
        var text = new EmojiTextBlock { Text = "使用者說的話", TextWrapping = TextWrapping.Wrap };
        var window = new Window { Width = 400, Height = 200, Content = new StackPanel { Children = { text } } };
        window.Show();
        window.UpdateLayout();

        Assert.IsAssignableFrom<SelectableTextBlock>(text);
        Assert.False(text.IsTabStop);
        var from = text.TranslatePoint(new Point(2, text.Bounds.Height / 2), window)!.Value;
        var to = text.TranslatePoint(new Point(text.Bounds.Width - 2, text.Bounds.Height / 2), window)!.Value;
        Drag(window, from, to);
        Assert.False(string.IsNullOrEmpty(text.SelectedText));
    }
}
