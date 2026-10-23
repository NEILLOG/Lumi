using Avalonia;
using Lumi.Core.Models;

namespace Lumi.App.Views;

/// <summary>一個螢幕的可用範圍與縮放比例（螢幕像素＋縮放）。</summary>
public readonly record struct WindowScreen(PixelRect WorkingArea, double Scaling);

/// <summary>還原後的視窗：位置是螢幕像素，寬高是 DIP（與 Avalonia 的 Width／Height 相同），<paramref name="Maximized"/> 另外套用。</summary>
public sealed record WindowPlacement(PixelPoint Position, double Width, double Height, bool Maximized);

/// <summary>
/// 工作台視窗位置與大小的還原規則（M9 9D）。純函式，仿 <c>SpritePlacementRules</c>，不碰視窗。
/// <para>
/// 單位假設（Avalonia 的約定，Windows 與 macOS 相同，由 <c>WindowPlacementRulesTests</c> 鎖住）：
/// <list type="bullet">
/// <item>位置（<see cref="PixelPoint"/>、<see cref="PixelRect"/>）是螢幕像素（實體像素）。Windows 是像素；
/// macOS 在 Avalonia 也以像素表示，點數（pt）只在 AppKit 內部使用，不進入這裡。</item>
/// <item>寬高（<see cref="WindowSettings.Width"/>）是 DIP（邏輯像素）：還原時照存的 DIP 值設定 Width／Height，
/// 不因縮放改變，視窗在 1× 與 2× 螢幕之間移動時邏輯大小不變。</item>
/// <item>範圍檢查（不大於螢幕聯集、標題列在螢幕內）要比較的是螢幕像素，所以 DIP 要乘上<b>目標螢幕目前的</b>縮放比例再比。
/// 這就是「縮放改變時重新換算」：同一組 DIP 大小在 150% 螢幕上佔 1.5 倍的實體像素，可能就放不下而退回預設。</item>
/// </list>
/// </para>
/// </summary>
public static class WindowPlacementRules
{
    /// <summary>標題列的檢查區域（DIP）：視窗左上角這一塊要完整落在某一個螢幕的工作區內。</summary>
    public static readonly Size TitleBarProbe = new(100, 40);

    /// <summary>
    /// 回傳可用的位置與大小；回 null 表示「退回置中的預設大小」（沒有儲存值、尺寸不合法、超出螢幕、或標題列不在任何一個螢幕內）。
    /// </summary>
    public static WindowPlacement? Restore(WindowSettings? saved, IReadOnlyList<WindowScreen> screens, double minWidth, double minHeight)
    {
        if (saved is null || screens.Count == 0)
            return null;

        var s = saved.Normalize();
        if (s.Scaling <= 0)                                  // 縮放不合法：無法換算，不冒險
            return null;
        if (s.Width < minWidth || s.Height < minHeight)      // 寬高為 0 的情況（NaN 或負數）也在這裡被擋掉
            return null;

        var target = TitleBarScreen(s, screens);
        if (target is null)                                  // 標題列不在任何螢幕內（拔掉外接螢幕、位置被移到螢幕外）
            return null;

        var union = Union(screens);
        var physicalWidth = s.Width * target.Value.Scaling;
        var physicalHeight = s.Height * target.Value.Scaling;
        if (physicalWidth > union.Width || physicalHeight > union.Height)   // 比所有螢幕加起來還大
            return null;

        return new WindowPlacement(new PixelPoint(s.X, s.Y), s.Width, s.Height, s.Maximized);
    }

    /// <summary>
    /// 標題列區域（左上角 100×40 DIP，換算成該螢幕的像素，並不超過視窗本身）完整落在哪一個螢幕的工作區內。
    /// 跨在兩個螢幕之間就不算（回 null），避免視窗一半在螢幕外。
    /// </summary>
    private static WindowScreen? TitleBarScreen(WindowSettings s, IReadOnlyList<WindowScreen> screens)
    {
        foreach (var screen in screens)
        {
            var scale = screen.Scaling;
            if (!(scale > 0))
                continue;

            var physicalWidth = Math.Min(Math.Round(TitleBarProbe.Width * scale), Math.Round(s.Width * scale));
            var physicalHeight = Math.Min(Math.Round(TitleBarProbe.Height * scale), Math.Round(s.Height * scale));
            var area = screen.WorkingArea;
            var left = s.X;
            var top = s.Y;
            var right = left + physicalWidth;
            var bottom = top + physicalHeight;

            if (left >= area.X && top >= area.Y && right <= area.Right && bottom <= area.Bottom)
                return screen;
        }
        return null;
    }

    /// <summary>所有螢幕工作區的外接矩形（螢幕像素）。</summary>
    private static PixelRect Union(IReadOnlyList<WindowScreen> screens)
    {
        var left = screens.Min(s => s.WorkingArea.X);
        var top = screens.Min(s => s.WorkingArea.Y);
        var right = screens.Max(s => s.WorkingArea.Right);
        var bottom = screens.Max(s => s.WorkingArea.Bottom);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
