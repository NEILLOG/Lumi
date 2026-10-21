using System.Globalization;
using Avalonia.Media;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// WCAG 2.x 的相對亮度與對比計算（原本在 <c>PaletteContrastTests</c>，抽出來讓色票檔對比與 Headless 的實際渲染對比共用）。
/// </summary>
public static class Wcag
{
    /// <summary>文字對比下限（ui-guide.html 第 1 節）。</summary>
    public const double TextMinimum = 4.5;

    public static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        double Channel(int start)
        {
            var c = int.Parse(h.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return Linear(c);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    public static double Luminance(Color color) =>
        0.2126 * Linear(color.R / 255.0) + 0.7152 * Linear(color.G / 255.0) + 0.0722 * Linear(color.B / 255.0);

    public static double Ratio(double luminanceA, double luminanceB) =>
        (Math.Max(luminanceA, luminanceB) + 0.05) / (Math.Min(luminanceA, luminanceB) + 0.05);

    public static double Ratio(Color a, Color b) => Ratio(Luminance(a), Luminance(b));

    /// <summary>把半透明的 <paramref name="top"/> 疊在不透明的 <paramref name="bottom"/> 上（alpha 合成），結果不透明。</summary>
    public static Color Over(Color top, Color bottom, double extraOpacity = 1)
    {
        var alpha = top.A / 255.0 * extraOpacity;
        byte Mix(byte t, byte b) => (byte)Math.Round(t * alpha + b * (1 - alpha));
        return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
    }

    private static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}
