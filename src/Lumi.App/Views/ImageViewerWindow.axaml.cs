using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Lumi.App.Views;

/// <summary>看圖視窗：在 app 內放大檢視生成的圖片或塔羅牌面，可縮放與平移。</summary>
public partial class ImageViewerWindow : Window
{
    private const double MinScale = 0.05;
    private const double MaxScale = 16;
    private const double ButtonStep = 1.25;
    private const double WheelStep = 1.1;

    private readonly Bitmap? _bitmap;
    private readonly Control _content;

    /// <summary>內容的原始大小（100% 時的大小）；圖片讀不到時是 null，縮放按鈕不做事。</summary>
    private readonly Size? _contentSize;
    private double _scale = 1;
    private Point _offset;
    private bool _fitMode = true;
    private Point? _dragStart;
    private Point _dragOffset;

    public ImageViewerWindow() : this(null) { }

    /// <param name="displayName">標題要顯示的名稱；沒給時用路徑的檔名。</param>
    public ImageViewerWindow(string? path, string? displayName = null)
    {
        InitializeComponent();
        _content = Picture;
        if (path is not null)
        {
            Title = displayName ?? System.IO.Path.GetFileName(path);
            NameText.Text = Title;
            _bitmap = TryLoad(path);
        }
        if (_bitmap is not null)
        {
            Picture.Source = _bitmap;
            Picture.Width = _bitmap.Size.Width;
            Picture.Height = _bitmap.Size.Height;
            _contentSize = _bitmap.Size;
        }
        Closed += (_, _) => _bitmap?.Dispose();
        WireInteractions();
    }

    /// <summary>顯示畫好的控制項（點牌放大的牌面）；<paramref name="details"/> 放在下方，<paramref name="closed"/> 用來釋放內容用到的圖片。</summary>
    public ImageViewerWindow(Control content, Size contentSize, string title, Control? details, Action? closed = null)
    {
        InitializeComponent();
        Title = title;
        NameText.Text = title;
        Stage.Children.Remove(Picture);
        content.Width = contentSize.Width;
        content.Height = contentSize.Height;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        content.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
        content.IsHitTestVisible = false;   // 滾輪與拖曳都由 Stage 接
        Stage.Children.Add(content);
        _content = content;
        _contentSize = contentSize;
        if (details is not null)
        {
            DetailsHost.Content = details;
            DetailsHost.IsVisible = true;
        }
        if (closed is not null)
            Closed += (_, _) => closed();
        WireInteractions();
    }

    private void WireInteractions()
    {
        Stage.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                if (_fitMode) ApplyFit(); else Apply();
            }
        };
        Stage.PointerWheelChanged += OnWheel;
        Stage.PointerPressed += OnPressed;
        Stage.PointerMoved += OnMoved;
        Stage.PointerReleased += (_, _) => EndDrag();
        Stage.PointerCaptureLost += (_, _) => EndDrag();
        Stage.DoubleTapped += (_, e) => { if (_fitMode) ZoomAt(1, ViewportCenter(e)); else ApplyFit(); };
        ZoomInButton.Click += (_, _) => ZoomAt(_scale * ButtonStep, ViewportCenter());
        ZoomOutButton.Click += (_, _) => ZoomAt(_scale / ButtonStep, ViewportCenter());
        FitButton.Click += (_, _) => ApplyFit();
        ActualButton.Click += (_, _) => ZoomAt(1, ViewportCenter());
        CloseButton.Click += (_, _) => Close();
        KeyDown += OnKeyDown;
    }

    private static Bitmap? TryLoad(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private Size Viewport => Stage.Bounds.Size;

    private Point ViewportCenter(TappedEventArgs? e = null) => e is null
        ? new Point(Viewport.Width / 2, Viewport.Height / 2)
        : e.GetPosition(Stage);

    /// <summary>符合視窗：整張圖放得進去；小圖不放大超過 100%。</summary>
    private void ApplyFit()
    {
        if (_contentSize is not { } size || Viewport.Width <= 0 || Viewport.Height <= 0)
            return;
        _fitMode = true;
        _scale = Math.Min(1, Math.Min(Viewport.Width / size.Width, Viewport.Height / size.Height));
        _offset = new Point(0, 0);
        Apply();
    }

    /// <summary>以 <paramref name="anchor"/>（視窗座標）為中心縮放，讓錨點下的圖片位置不動。</summary>
    private void ZoomAt(double scale, Point anchor)
    {
        if (_contentSize is null)
            return;
        _fitMode = false;
        var next = Math.Clamp(scale, MinScale, MaxScale);
        var ratio = next / _scale;
        _offset = new Point(anchor.X - (anchor.X - _offset.X) * ratio, anchor.Y - (anchor.Y - _offset.Y) * ratio);
        _scale = next;
        Apply();
    }

    /// <summary>圖片比視窗小的那一軸置中；比視窗大的那一軸不讓邊緣離開視窗。</summary>
    private double Clamp(double offset, double imageLength, double viewportLength)
    {
        var scaled = imageLength * _scale;
        return scaled <= viewportLength
            ? (viewportLength - scaled) / 2
            : Math.Clamp(offset, viewportLength - scaled, 0);
    }

    private void Apply()
    {
        if (_contentSize is not { } size)
            return;
        _offset = new Point(Clamp(_offset.X, size.Width, Viewport.Width), Clamp(_offset.Y, size.Height, Viewport.Height));
        _content.RenderTransform = new MatrixTransform(new Matrix(_scale, 0, 0, _scale, _offset.X, _offset.Y));
        ZoomText.Text = (_scale * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        ZoomAt(e.Delta.Y > 0 ? _scale * WheelStep : _scale / WheelStep, e.GetPosition(Stage));
        e.Handled = true;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Stage).Properties.IsLeftButtonPressed)
            return;
        _dragStart = e.GetPosition(Stage);
        _dragOffset = _offset;
        e.Pointer.Capture(Stage);
        Stage.Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start)
            return;
        var now = e.GetPosition(Stage);
        _fitMode = false;
        _offset = new Point(_dragOffset.X + now.X - start.X, _dragOffset.Y + now.Y - start.Y);
        Apply();
    }

    private void EndDrag()
    {
        _dragStart = null;
        Stage.Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.OemPlus or Key.Add:
                ZoomAt(_scale * ButtonStep, ViewportCenter());
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomAt(_scale / ButtonStep, ViewportCenter());
                break;
            case Key.D0 or Key.NumPad0:
                ApplyFit();
                break;
            case Key.D1 or Key.NumPad1:
                ZoomAt(1, ViewportCenter());
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
