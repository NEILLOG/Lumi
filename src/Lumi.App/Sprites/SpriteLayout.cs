using Avalonia;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Sprites;

/// <summary>
/// 小精靈視窗的版面：由上而下是「氣泡預留區、角色（Outer）、輸入列」。視窗大小固定預留空間，
/// 不隨氣泡或輸入列的內容改變（避免拖曳與動畫時視窗一直變形）。位置以角色中心為準，切換檔位時不會跳動。
/// </summary>
public sealed record SpriteLayout(Size WindowSize, Point SpriteOrigin, Point SpriteCenter, Rect SpriteBox, double BubbleBottom, Rect InputBar)
{
    public static SpriteLayout Compute(SpriteOptions sprite, Point bubbleAnchor, SpriteShellOptions shell)
    {
        var width = Math.Max(shell.MinWindowWidth, sprite.Outer.Width);
        var origin = new Point((width - sprite.Outer.Width) / 2, shell.BubbleReserve);
        var box = sprite.BoxRect.Translate(new Vector(origin.X, origin.Y));
        var center = new Point(origin.X + sprite.Outer.Width / 2, origin.Y + sprite.Outer.Height / 2);

        var inputTop = box.Bottom + shell.InputGap;
        var input = new Rect((width - 300) / 2, inputTop, 300, shell.InputBarHeight);
        var height = input.Bottom + shell.BottomMargin;

        return new SpriteLayout(new Size(width, height), origin, center, box, origin.Y + bubbleAnchor.Y, input);
    }

    /// <summary>視窗左上角（螢幕像素）＝角色中心的螢幕座標減去角色中心在視窗內的位置。</summary>
    public PixelPoint WindowPositionFor(PixelPoint spriteCenterOnScreen, double desktopScaling) => new(
        spriteCenterOnScreen.X - (int)Math.Round(SpriteCenter.X * desktopScaling),
        spriteCenterOnScreen.Y - (int)Math.Round(SpriteCenter.Y * desktopScaling));

    public PixelPoint SpriteCenterOnScreen(PixelPoint windowPosition, double desktopScaling) => new(
        windowPosition.X + (int)Math.Round(SpriteCenter.X * desktopScaling),
        windowPosition.Y + (int)Math.Round(SpriteCenter.Y * desktopScaling));
}

/// <summary>一個螢幕的可用範圍（螢幕像素）。</summary>
public readonly record struct ScreenArea(PixelRect WorkingArea, bool IsPrimary);

public static class SpritePlacementRules
{
    /// <summary>
    /// 把角色中心夾到「中心所在的螢幕」的可用範圍內（角色框的一半留在螢幕裡）；
    /// 中心不在任何螢幕（例如拔掉外接螢幕）時，改放主螢幕右下角。
    /// </summary>
    public static PixelPoint Clamp(PixelPoint center, IReadOnlyList<ScreenArea> screens, double halfBoxPx, bool allowFallback = true)
    {
        if (screens.Count == 0)
            return center;

        var screen = screens.Cast<ScreenArea?>().FirstOrDefault(s => s!.Value.WorkingArea.Contains(center));
        if (screen is null)
        {
            if (!allowFallback)
                return center;
            var primary = screens.FirstOrDefault(s => s.IsPrimary, screens[0]).WorkingArea;
            return DefaultCenter(primary, halfBoxPx);
        }

        var area = screen.Value.WorkingArea;
        var r = (int)Math.Ceiling(halfBoxPx);
        return new PixelPoint(
            Math.Clamp(center.X, area.X + r, Math.Max(area.X + r, area.Right - r)),
            Math.Clamp(center.Y, area.Y + r, Math.Max(area.Y + r, area.Bottom - r)));
    }

    /// <summary>主螢幕右下角（留一點邊）。</summary>
    public static PixelPoint DefaultCenter(PixelRect area, double halfBoxPx)
    {
        var margin = (int)Math.Ceiling(halfBoxPx) + 24;
        return new PixelPoint(area.Right - margin, area.Bottom - margin);
    }
}
