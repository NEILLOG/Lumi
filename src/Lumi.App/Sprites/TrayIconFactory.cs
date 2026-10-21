using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Lumi.App.Sprites;

/// <summary>托盤圖示：程式畫的簡單光球（M11 再換正式圖示）。不分深淺色版本。</summary>
public static class TrayIconFactory
{
    public static WindowIcon CreateOrbIcon(int size = 64)
    {
        var orb = new Ellipse
        {
            Width = size,
            Height = size,
            Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                RadiusX = RelativeScalar.Parse("90%"),
                RadiusY = RelativeScalar.Parse("90%"),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromRgb(0xFF, 0xE9, 0xC7), 0.3),
                    new GradientStop(Color.FromRgb(0xF5, 0xB7, 0x6A), 0.7),
                    new GradientStop(Color.FromRgb(0xC9, 0x78, 0x3A), 1),
                },
            },
        };
        orb.Measure(new Size(size, size));
        orb.Arrange(new Rect(0, 0, size, size));

        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        bitmap.Render(orb);
        return new WindowIcon(bitmap);
    }
}
