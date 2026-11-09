using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.Resources;
using Lumi.Core.Diagrams;

namespace Lumi.App.Tests.Headless;

/// <summary>回覆裡的 Mermaid 程式碼區塊畫成圖片：成功顯示圖、可切換原始碼、點圖放大；畫不出來時顯示程式碼與原因。</summary>
public class MermaidRenderingTests
{
    // 1×1 的透明 PNG
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    private sealed class FakeRenderer(DiagramRenderResult result) : IDiagramRenderer
    {
        public List<string> Codes { get; } = [];

        public Task<DiagramRenderResult> RenderMermaidAsync(string code, bool dark, CancellationToken ct = default)
        {
            Codes.Add(code);
            return Task.FromResult(result);
        }
    }

    private sealed record Shown(MarkdownView View, Window Window, List<(Control Content, Size Size)> Opened);

    private static Shown Show(string markdown, IDiagramRenderer renderer)
    {
        var opened = new List<(Control, Size)>();
        var services = new MarkdownServices(_ => Task.CompletedTask, _ => Task.CompletedTask, renderer, (c, s) => opened.Add((c, s)));
        var view = new MarkdownView { Services = services, Markdown = markdown };
        var window = new Window { Width = 640, Height = 800, Content = view };
        window.Show();
        view.Flush();
        window.UpdateLayout();
        return new Shown(view, window, opened);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control => root.GetLogicalDescendants().OfType<T>();

    private static string Fence(string code) => "說明如下：\n\n```mermaid\n" + code + "\n```\n";

    [AvaloniaFact]
    public async Task MermaidBlock_IsShownAsAPicture_AndCanSwitchToTheCode()
    {
        var code = "flowchart TD\n  A --> B  %% " + Guid.NewGuid();
        var renderer = new FakeRenderer(DiagramRenderResult.Ok(new DiagramImage(TinyPng, 320, 200)));
        var shown = Show(Fence(code), renderer);
        var block = Assert.IsType<MermaidBlock>(shown.View.RenderedBlocks[1]);
        Assert.Contains(All<TextBlock>(block), t => t.Text == Strings.Diagram_Rendering);

        await WaitUntil(() => block.ShowsDiagram);

        Assert.Equal([code], renderer.Codes);
        var picture = All<Image>(block).Single(i => i.Name == "DiagramImage");
        Assert.Equal(320, picture.MaxWidth);
        Assert.Equal(MermaidBlock.MaxDisplayHeight, picture.MaxHeight);

        var toggle = All<Button>(block).Single(b => b.Name == "DiagramToggle");
        Assert.True(toggle.IsVisible);
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        shown.Window.UpdateLayout();

        Assert.False(block.ShowsDiagram);
        Assert.Contains(All<EmojiTextBlock>(block), t => t.Text == code && t.IsEffectivelyVisible);
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task ClickingThePicture_OpensTheViewerAtItsRealSize()
    {
        var renderer = new FakeRenderer(DiagramRenderResult.Ok(new DiagramImage(TinyPng, 320, 200)));
        var shown = Show(Fence("graph LR\n  A-->B %% " + Guid.NewGuid()), renderer);
        var block = Assert.IsType<MermaidBlock>(shown.View.RenderedBlocks[1]);
        await WaitUntil(() => block.ShowsDiagram);
        var picture = All<Image>(block).Single(i => i.Name == "DiagramImage");

        picture.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(picture, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true),
            picture, default, 0, new Avalonia.Input.PointerPointProperties(), Avalonia.Input.KeyModifiers.None, Avalonia.Input.MouseButton.Left));

        var (_, size) = Assert.Single(shown.Opened);
        Assert.Equal(new Size(320, 200), size);
        shown.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhenItCannotBeDrawn_TheCodeAndTheReasonAreShown()
    {
        var code = "graph TD\n  A--> %% " + Guid.NewGuid();
        var shown = Show(Fence(code), new FakeRenderer(DiagramRenderResult.Fail("流程圖的語法有誤：Parse error")));
        var block = Assert.IsType<MermaidBlock>(shown.View.RenderedBlocks[1]);

        await WaitUntil(() => All<EmojiTextBlock>(block).Any(t => t.Text == code));

        Assert.False(block.ShowsDiagram);
        Assert.Contains(All<TextBlock>(block), t => t.Text!.Contains("流程圖的語法有誤", StringComparison.Ordinal));
        Assert.False(All<Button>(block).Single(b => b.Name == "DiagramToggle").IsVisible);
        shown.Window.Close();
    }

    [AvaloniaFact]
    public void OtherCodeBlocks_AreUnchanged()
    {
        var shown = Show("```python\nprint('hi')\n```", new FakeRenderer(DiagramRenderResult.Fail("不該被呼叫")));

        Assert.IsNotType<MermaidBlock>(shown.View.RenderedBlocks[0]);
        shown.Window.Close();
    }
}
