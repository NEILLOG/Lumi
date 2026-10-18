using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Lumi.App.Markdown;

/// <summary>
/// 顯示一段 Markdown。串流時文字會一直變長：更新排成 <c>Background</c> 優先權（同一個畫面週期內連續的多個
/// <c>TextDelta</c> 合併成一次），每次整份重新解析，但和上一次逐個比對最上層區塊的原文，
/// 沒變的沿用原本的控制項，只從第一個有變的區塊開始往後重建——串流時通常只有最後一個區塊在變。
/// 回應完成（<see cref="IsStreaming"/> 變 false）時立刻渲染最後一次。
/// </summary>
public sealed class MarkdownView : Decorator
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<bool> IsStreamingProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(IsStreaming));

    private readonly StackPanel _panel = new() { Spacing = 10 };
    private readonly List<(string Source, Control Control)> _rendered = [];
    private MarkdownRenderer? _renderer;
    private bool _renderPending;

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.OnContentChanged());
        IsStreamingProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.OnContentChanged());
    }

    public MarkdownView()
    {
        Child = _panel;
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsStreaming
    {
        get => GetValue(IsStreamingProperty);
        set => SetValue(IsStreamingProperty, value);
    }

    /// <summary>連結與複製的實作；沒設定時用目前視窗的啟動器與剪貼簿。測試可以換成假的。</summary>
    public MarkdownServices? Services { get; set; }

    /// <summary>目前畫面上每個最上層區塊的控制項（測試用：確認增量更新沒有重建沒變的區塊）。</summary>
    public IReadOnlyList<Control> RenderedBlocks => _rendered.Select(r => r.Control).ToList();

    private void OnContentChanged()
    {
        if (!IsStreaming)
        {
            Flush();
            return;
        }

        if (_renderPending)
            return;
        _renderPending = true;
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    /// <summary>馬上把目前的文字渲染出來（串流中的排程更新也會呼叫它）。</summary>
    public void Flush()
    {
        _renderPending = false;
        var blocks = MarkdownParser.Parse(Markdown ?? "");

        var common = 0;
        while (common < _rendered.Count && common < blocks.Count && _rendered[common].Source == blocks[common].Source)
            common++;

        if (common < _rendered.Count)
        {
            _panel.Children.RemoveRange(common, _rendered.Count - common);
            _rendered.RemoveRange(common, _rendered.Count - common);
        }

        var renderer = _renderer ??= new MarkdownRenderer(Services ?? CreateDefaultServices());
        for (var i = common; i < blocks.Count; i++)
        {
            var control = renderer.Render(blocks[i]);
            _panel.Children.Add(control);
            _rendered.Add((blocks[i].Source, control));
        }
    }

    private MarkdownServices CreateDefaultServices() => new(
        async uri =>
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(uri);
        },
        async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        });
}
