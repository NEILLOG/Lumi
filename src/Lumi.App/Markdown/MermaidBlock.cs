using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.Core.Diagrams;

namespace Lumi.App.Markdown;

/// <summary>
/// Mermaid 程式碼區塊：畫成圖片顯示（點圖用看圖視窗放大），外框與標題列和一般程式碼區塊相同。
/// 標題列有「原始碼」／「流程圖」切換與「複製」（複製的是程式碼）；畫不出來時顯示程式碼與原因。
/// 放上畫面 <see cref="SettleDelay"/> 後才開始畫：串流中程式碼還在變，區塊會一直被換掉，不必每次都畫。
/// </summary>
internal sealed class MermaidBlock : Border
{
    /// <summary>區塊穩定多久才開始畫。</summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>圖在對話裡最高多少（太高的圖縮小，點開看原尺寸）。</summary>
    internal const double MaxDisplayHeight = 480;

    private readonly string _code;
    private readonly MarkdownServices _services;
    private readonly IDiagramRenderer _renderer;
    private readonly ContentControl _body = new();
    private readonly Control _codeView;
    private readonly Button _toggle;
    private readonly TextBlock _toggleLabel;
    private readonly DispatcherTimer _settle = new() { Interval = SettleDelay };
    private CancellationTokenSource? _cts;
    private Control? _diagramView;
    private bool _showCode;

    public MermaidBlock(string code, MarkdownServices services, Control codeView, Button copy)
    {
        _code = code;
        _services = services;
        _renderer = services.Diagrams ?? new NullDiagramRenderer();
        _codeView = codeView;
        Classes.Add("md-codeblock");
        Classes.Add("md-diagram");

        var language = new TextBlock { Text = "mermaid", VerticalAlignment = VerticalAlignment.Center, Classes = { "md-codelang" } };
        _toggleLabel = new TextBlock { Text = Strings.Diagram_ShowCode, VerticalAlignment = VerticalAlignment.Center };
        _toggle = new Button
        {
            Name = "DiagramToggle",
            Classes = { "md-copy" },
            VerticalAlignment = VerticalAlignment.Center,
            Content = _toggleLabel,
            IsVisible = false,
        };
        _toggle.Click += (_, _) => SetShowCode(!_showCode);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_toggle);
        buttons.Children.Add(copy);
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(buttons, 1);
        headerGrid.Children.Add(language);
        headerGrid.Children.Add(buttons);

        var stack = new StackPanel();
        stack.Children.Add(new Border { Classes = { "md-codeheader" }, Child = headerGrid });
        stack.Children.Add(_body);
        Child = stack;

        _body.Content = Status(Strings.Diagram_Rendering);
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = RenderAsync();
        };
        AttachedToVisualTree += (_, _) => _settle.Start();
        DetachedFromVisualTree += (_, _) =>
        {
            _settle.Stop();
            _cts?.Cancel();
        };
    }

    /// <summary>目前是不是顯示著畫好的圖（測試用）。</summary>
    internal bool ShowsDiagram => _diagramView is not null && !_showCode;

    private static bool IsDark() => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private async Task RenderAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var dark = IsDark();
        DiagramRenderResult result;
        try
        {
            result = DiagramCache.TryGet(_code, dark, out var cached)
                ? cached
                : DiagramCache.Remember(_code, dark, await _renderer.RenderMermaidAsync(_code, dark, ct));
        }
        catch (OperationCanceledException)
        {
            return;   // 區塊被換掉了
        }
        if (ct.IsCancellationRequested)
            return;

        if (result.Image is { } image)
        {
            _diagramView = CreateDiagramView(image);
            _toggle.IsVisible = true;
            SetShowCode(false);
        }
        else
        {
            // 畫不出來：顯示程式碼，上方一行說明原因
            var panel = new StackPanel();
            panel.Children.Add(Status(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Diagram_FailedFormat, result.Problem)));
            panel.Children.Add(_codeView);
            _body.Content = panel;
        }
    }

    private void SetShowCode(bool showCode)
    {
        _showCode = showCode;
        _toggleLabel.Text = showCode ? Strings.Diagram_ShowDiagram : Strings.Diagram_ShowCode;
        _body.Content = showCode ? _codeView : _diagramView;
    }

    private Control CreateDiagramView(DiagramImage image)
    {
        var bitmap = new Bitmap(new MemoryStream(image.Png));
        var picture = new Image
        {
            Name = "DiagramImage",
            Source = bitmap,
            Stretch = Avalonia.Media.Stretch.Uniform,
            MaxWidth = image.Width,
            MaxHeight = MaxDisplayHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(picture, Strings.Diagram_ViewLarger);
        AutomationProperties.SetName(picture, Strings.Diagram_ViewerTitle);
        picture.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                _services.OpenImage?.Invoke(new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.Fill }, new Size(image.Width, image.Height));
        };
        return new Border { Padding = new Thickness(16, 12, 16, 12), Child = picture };
    }

    private static Control Status(string text) => new TextBlock
    {
        Text = text,
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Margin = new Thickness(16, 12, 16, 0),
        Classes = { "md-diagram-status" },
    };
}

/// <summary>
/// 畫好的圖暫存在記憶體裡（同一段程式碼、同一個主題不重畫，例如切換對話再切回來）。
/// 語法錯誤也記住；其他失敗（沒有瀏覽器、逾時）不記，下次再試。最多 <see cref="Capacity"/> 筆，超過就丟最舊的。
/// </summary>
internal static class DiagramCache
{
    internal const int Capacity = 32;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string, bool), DiagramRenderResult> Items = [];
    private static readonly Queue<(string, bool)> Order = new();

    public static bool TryGet(string code, bool dark, out DiagramRenderResult result)
    {
        lock (Gate)
            return Items.TryGetValue((code, dark), out result!);
    }

    public static DiagramRenderResult Remember(string code, bool dark, DiagramRenderResult result)
    {
        if (!result.Succeeded && result.Problem?.StartsWith("流程圖的語法有誤", StringComparison.Ordinal) != true)
            return result;
        lock (Gate)
        {
            if (Items.TryAdd((code, dark), result))
            {
                Order.Enqueue((code, dark));
                while (Order.Count > Capacity)
                    Items.Remove(Order.Dequeue());
            }
        }
        return result;
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Items.Clear();
            Order.Clear();
        }
    }
}
