using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Sprites;

/// <summary>
/// 工作台角色的舞台（M27）：蓋在標題列與工具列上，下緣切齊分隔線。量測時回傳 0，<b>不會把上方兩列撐高</b>；
/// 超出下緣的部分被切掉（探頭時沉到線後面，也點不到）。自己沒有背景，空白處的滑鼠穿透到標題列。
/// </summary>
public sealed class WorkbenchSpriteStage : Panel
{
    /// <summary>工具列下緣的分隔線粗細：舞台停在線的上緣。</summary>
    private const double DividerThickness = 1;

    private double _hostLeft;

    public WorkbenchSpriteStage()
    {
        ClipToBounds = true;
        Margin = new Thickness(0, 0, 0, DividerThickness);
    }

    /// <summary>角色外框的左緣（舞台座標）。</summary>
    public double HostLeft
    {
        get => _hostLeft;
        set
        {
            if (_hostLeft.Equals(value))
                return;
            _hostLeft = value;
            InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(Size.Infinity);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
            child.Arrange(new Rect(HostLeft, finalSize.Height - child.DesiredSize.Height, child.DesiredSize.Width, child.DesiredSize.Height));
        return finalSize;
    }
}
