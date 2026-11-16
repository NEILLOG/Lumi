using Avalonia;

namespace Lumi.App.Views;

/// <summary>
/// 並排的版面計算（M35 35G，純函式，不碰視窗）：工作區左半是工作台、右半是研究本視窗。
/// 工作區寬度（DIP）不到 <see cref="MinDipWidth"/> 就不能並排（回 null）。
/// </summary>
public static class SideBySideLayout
{
    /// <summary>並排需要的最小工作區寬度（DIP）。</summary>
    public const double MinDipWidth = 1000;

    /// <param name="workingArea">螢幕的工作區（螢幕像素）。</param>
    /// <param name="scaling">該螢幕的縮放比例。</param>
    /// <returns>左右兩個區域（螢幕像素）；寬度是奇數時左邊多 1 像素；高度是整個工作區。太窄或縮放不合法回 null。</returns>
    public static (PixelRect Left, PixelRect Right)? Compute(PixelRect workingArea, double scaling)
    {
        if (!(scaling > 0) || workingArea.Width / scaling < MinDipWidth)
            return null;

        var leftWidth = (workingArea.Width + 1) / 2;
        var rightWidth = workingArea.Width - leftWidth;
        var left = new PixelRect(workingArea.X, workingArea.Y, leftWidth, workingArea.Height);
        var right = new PixelRect(workingArea.X + leftWidth, workingArea.Y, rightWidth, workingArea.Height);
        return (left, right);
    }
}
