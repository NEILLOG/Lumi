using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Assets;

/// <summary>
/// 把 sprite sheet 的某一格畫進角色的 <c>Visual</c>（大小是 <c>Outer</c>）。縮放與貼齊的規則在 <see cref="SpriteSheetLayout"/>；
/// <c>rendering: pixelated</c> 時用最近鄰（不做平滑），否則高品質縮放。
/// </summary>
internal sealed class SpriteSheetControl : Control
{
    private Bitmap? _image;
    private Rect _source;
    private readonly Size _cell;
    private readonly int? _pixelGrid;

    public SpriteSheetControl(Size outer, Size cell, int? pixelGrid, SpriteRendering rendering)
    {
        _cell = cell;
        _pixelGrid = pixelGrid;
        Width = outer.Width;
        Height = outer.Height;
        IsHitTestVisible = false;   // 點擊由殼用 HitTest 判斷，控制項本身不吃事件
        RenderOptions.SetBitmapInterpolationMode(this, rendering == SpriteRendering.Pixelated ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
    }

    /// <summary>測試用：指定縮放比（headless 的視窗固定是 1 倍，要驗證 2 倍螢幕時用）。</summary>
    internal double? RenderScalingOverride { get; set; }

    /// <summary>目前用來換算的縮放比。還沒掛上視窗時用 1；掛上之後跟著視窗（換到另一個螢幕時會變）。</summary>
    public double RenderScaling => RenderScalingOverride ?? (this.GetVisualRoot() as TopLevel)?.RenderScaling ?? 1;

    /// <summary>目前這一格畫在 <c>Visual</c> 裡的範圍（命中判斷與繪製用同一個）。</summary>
    public Rect Destination => SpriteSheetLayout.Destination(new Size(Width, Height), _cell, _pixelGrid, RenderScaling);

    /// <summary>換成要畫的那一格；<paramref name="image"/> 是 null（解碼失敗）就什麼都不畫。</summary>
    public void Show(Bitmap? image, Rect source)
    {
        if (ReferenceEquals(image, _image) && source == _source)
            return;
        _image = image;
        _source = source;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_image is null)
            return;
        var destination = Destination;
        if (destination.Width > 0 && destination.Height > 0)
            context.DrawImage(_image, _source, destination);
    }
}
