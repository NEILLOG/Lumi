using Avalonia;

namespace Lumi.Sprites.Abstractions;

/// <summary>
/// 殼建立角色時給的尺寸（已經過 <see cref="SpriteLimits.Fit"/> 縮放）。
/// 角色的 <see cref="ISpriteView.Visual"/> 大小必須是 <see cref="Outer"/>，角色框在正中央。
/// </summary>
/// <param name="Diameter">尺寸檔位：光球直徑（64／80／104），角色以它為比例基準。</param>
/// <param name="Box">角色框（本體範圍）。</param>
/// <param name="Outer">角色框加上特效外擴後的大小，也就是 Visual 的大小。</param>
public sealed record SpriteOptions(double Diameter, Size Box, Size Outer)
{
    /// <summary>角色框在 <see cref="Outer"/> 裡的位置。</summary>
    public Rect BoxRect => new(
        (Outer.Width - Box.Width) / 2, (Outer.Height - Box.Height) / 2, Box.Width, Box.Height);
}
