using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 凱爾特十字的編號圓點（plan/25 25D；ui-spec 3.1）。位置（中心）由 <see cref="TarotStageLayout"/> 決定，
/// 這裡只管大小與外觀。高亮時中心不動，只變大。
/// </summary>
public partial class TarotNumberBadge : UserControl
{
    /// <summary>一般圓點直徑（一位數）。</summary>
    private const double Diameter = 18;

    /// <summary>高亮圓點直徑（一位數）。</summary>
    private const double HighlightDiameter = 24;

    /// <summary>高亮外圈的寬度（每邊）。</summary>
    private const double RingWidth = 2;

    public TarotNumberBadge()
    {
        InitializeComponent();
    }

    /// <summary>圓點的外框尺寸（像素）。兩位數（10）較寬。一般 18×18，高亮含外圈 28×28。</summary>
    public static (double Width, double Height) SizeOf(int number, bool highlighted)
    {
        var wide = number >= 10;
        if (highlighted)
            return (wide ? HighlightDiameter + 4 + 4 : HighlightDiameter + 2 * RingWidth, HighlightDiameter + 2 * RingWidth);
        return (wide ? 22 : Diameter, Diameter);
    }

    /// <summary>設定編號與高亮狀態，並依狀態換大小與外觀。</summary>
    public void Set(int number, bool highlighted)
    {
        var (width, height) = SizeOf(number, highlighted);
        Width = width;
        Height = height;
        Number.Text = number.ToString(CultureInfo.InvariantCulture);
        Root.Classes.Set("highlighted", highlighted);
        Disc.Classes.Set("highlighted", highlighted);
        var discHeight = highlighted ? HighlightDiameter : Diameter;
        Root.CornerRadius = new Avalonia.CornerRadius(height / 2);
        Disc.CornerRadius = new Avalonia.CornerRadius(discHeight / 2);
    }
}
