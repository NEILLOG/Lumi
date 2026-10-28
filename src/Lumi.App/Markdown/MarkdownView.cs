using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;

namespace Lumi.App.Markdown;

/// <summary>
/// 顯示一段 Markdown。串流時文字會一直變長：更新排成 <c>Background</c> 優先權（同一個畫面週期內連續的多個
/// <c>TextDelta</c> 合併成一次），每次整份重新解析，但和上一次逐個比對最上層區塊的原文，
/// 沒變的沿用原本的控制項，只從第一個有變的區塊開始往後重建——串流時通常只有最後一個區塊在變。
/// 回應完成（<see cref="IsStreaming"/> 變 false）時立刻渲染最後一次。
/// <para>
/// 選取：每個段落、清單項目、表格儲存格、程式碼都是各自的 <see cref="SelectableTextBlock"/>，Avalonia 只能在單一控制項內選取。
/// 所以左鍵在文字上按下時由這裡接手（Tunnel 階段先攔下）：記住起點，拖曳時把起點到目前位置之間的每個文字區塊設好
/// <c>SelectionStart/End</c>，複製時依序接起來。沒拖曳就放開＝點擊（點到連結就開啟）。雙擊、三擊交給文字區塊自己選字、選整段。
/// </para>
/// </summary>
public sealed class MarkdownView : Decorator, ITextSelectionScope
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
        // 要能拿到焦點才收得到 Ctrl/Cmd+C；不放進 Tab 順序（每則訊息都停一下會很吵）
        Focusable = true;
        IsTabStop = false;
        AddHandler(PointerPressedEvent, OnSelectionPointerPressed, RoutingStrategies.Tunnel);
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

    // ---- 跨段落選取 ----

    /// <summary>點擊與拖曳的分界（像素）：移動超過這個距離就是在選取，不是點擊。</summary>
    private const double ClickSlop = 4;

    /// <summary>「選到這個區塊的結尾」（拖到最後一個區塊下面時用）。</summary>
    private const int ToEnd = int.MaxValue;

    private static readonly Cursor IBeamCursor = new(StandardCursorType.Ibeam);

    private (SelectableTextBlock Block, int Position)? _anchor;
    private Point _pressPoint;
    private bool _dragging;

    /// <summary>目前選取的文字：每個有選取的文字區塊依序用換行接起來（也包含雙擊在單一區塊裡選的字）。</summary>
    public string SelectedText =>
        string.Join("\n", TextBlocks().Select(b => b.SelectedText).Where(t => !string.IsNullOrEmpty(t)));

    /// <summary>所有可選取的文字區塊，依文件順序（視覺樹深度優先＝段落、清單、表格逐列、程式碼的順序）。</summary>
    private List<SelectableTextBlock> TextBlocks() =>
        _panel.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();

    private void OnSelectionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed
            || (e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true) is not { } block)
            return;

        ClearSelection();
        if (e.ClickCount > 1)
            return;   // 雙擊選字、三擊選整段：交給文字區塊自己處理

        _anchor = (block, PositionIn(block, e.GetPosition(block)));
        _pressPoint = point.Position;
        _dragging = false;
        Focus();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_anchor is not { } anchor || !ReferenceEquals(e.Pointer.Captured, this))
            return;

        var position = e.GetPosition(this);
        if (!_dragging)
        {
            var moved = position - _pressPoint;
            if (Math.Sqrt(moved.X * moved.X + moved.Y * moved.Y) <= ClickSlop)
                return;
            _dragging = true;
            Cursor = IBeamCursor;
        }

        var blocks = TextBlocks();
        var anchorIndex = blocks.IndexOf(anchor.Block);
        if (anchorIndex < 0)
        {
            // 串流中起點所在的區塊被重建了：這次拖曳作廢
            EndPress(e.Pointer);
            return;
        }
        Select(blocks, (anchorIndex, anchor.Position), HitTest(blocks, position));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_anchor is not { } anchor || e.InitialPressMouseButton != MouseButton.Left)
            return;

        var wasDragging = _dragging;
        EndPress(e.Pointer);
        e.Handled = true;
        // 沒拖曳就放開＝點擊：點到連結就開啟
        if (!wasDragging && anchor.Block is MarkdownTextBlock text && text.LinkAt(e.GetPosition(text)) is { } link)
            text.OpenLink(link);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _anchor = null;
        _dragging = false;
        Cursor = null;
    }

    private void EndPress(IPointer pointer)
    {
        _anchor = null;
        _dragging = false;
        Cursor = null;
        if (ReferenceEquals(pointer.Captured, this))
            pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;   // 焦點在單一文字區塊裡（雙擊選字後）時，它自己會處理複製

        var copy = TopLevel.GetTopLevel(this)?.PlatformSettings?.HotkeyConfiguration.Copy;
        if (copy is not null && copy.Any(g => g.Matches(e)) && SelectedText is { Length: > 0 } text)
        {
            e.Handled = true;
            _ = CopyAsync(text);
        }
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // 剪貼簿被其他程式佔用時放棄這次複製
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!ReferenceEquals(e.Source, this))
            return;

        // 焦點移到自己裡面的文字區塊、或右鍵選單開著（要用它複製）時保留選取；移到別處才清掉
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused && this.IsVisualAncestorOf(focused))
            return;
        var blocks = TextBlocks();
        if (blocks.Any(b => b.ContextMenu?.IsOpen == true))
            return;
        foreach (var block in blocks)
            Clear(block);
    }

    /// <summary>把點（相對於這個控制項）換算成「第幾個文字區塊的第幾個字」。</summary>
    private (int Index, int Position) HitTest(List<SelectableTextBlock> blocks, Point point)
    {
        // 點在某些區塊的上下範圍內：取水平距離最近的那個（表格同一列的儲存格），把點夾進它的範圍再換算——在行尾右邊就是行尾
        var best = -1;
        var bestDx = double.MaxValue;
        var rects = new Rect?[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].TranslatePoint(default, this) is not { } origin)
                continue;
            var rect = new Rect(origin, blocks[i].Bounds.Size);
            rects[i] = rect;
            if (point.Y < rect.Top || point.Y > rect.Bottom)
                continue;
            var dx = point.X < rect.Left ? rect.Left - point.X : point.X > rect.Right ? point.X - rect.Right : 0;
            if (dx < bestDx)
            {
                best = i;
                bestDx = dx;
            }
        }

        if (best >= 0)
        {
            var r = rects[best]!.Value;
            var clamped = new Point(Math.Clamp(point.X, r.Left, r.Right), Math.Clamp(point.Y, r.Top, r.Bottom));
            return (best, PositionIn(blocks[best], clamped - r.TopLeft));
        }

        // 點在區塊之間的空隙：算在下一個區塊的開頭；比最後一個還下面就是最後一個的結尾
        for (var i = 0; i < blocks.Count; i++)
        {
            if (rects[i] is { } rect && rect.Top > point.Y)
                return (i, 0);
        }
        return (blocks.Count - 1, ToEnd);
    }

    private static int PositionIn(SelectableTextBlock block, Point local) =>
        block.TextLayout.HitTestPoint(new Point(local.X - block.Padding.Left, local.Y - block.Padding.Top)).TextPosition;

    private static void Select(List<SelectableTextBlock> blocks, (int Index, int Position) a, (int Index, int Position) b)
    {
        var (from, to) = a.Index < b.Index || (a.Index == b.Index && a.Position <= b.Position) ? (a, b) : (b, a);
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i < from.Index || i > to.Index)
                Clear(blocks[i]);
            else
                SetSelection(blocks[i], i == from.Index ? from.Position : 0, i == to.Index ? to.Position : ToEnd);
        }
    }

    private static void SetSelection(SelectableTextBlock block, int start, int end)
    {
        if (end == ToEnd)
        {
            block.SelectAll();   // SelectionEnd 會是這個區塊的文字長度
            block.SelectionStart = Math.Min(start, block.SelectionEnd);
        }
        else
        {
            block.SelectionStart = start;
            block.SelectionEnd = end;
        }
    }

    private static void Clear(SelectableTextBlock block)
    {
        if (block.SelectionStart == 0 && block.SelectionEnd == 0)
            return;
        block.SelectionStart = 0;
        block.SelectionEnd = 0;
    }

    private void ClearSelection()
    {
        foreach (var block in TextBlocks())
            Clear(block);
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
