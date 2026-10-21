namespace Lumi.App.Views.Responsive;

/// <summary>寬度三段（RWD原則 第 2 節）。</summary>
public enum WidthClass { Narrow, Compact, Wide }

/// <summary>高度兩段（RWD原則 第 5 節）。</summary>
public enum HeightClass { Normal, Short, VeryShort }

/// <summary>
/// 斷點判定（純函式，不碰視窗，可以測試）。跨越斷點時要有 ±8 px 的緩衝（hysteresis），
/// 避免視窗停在 619/620、999/1000 附近時反覆切換版面（RWD原則 第 8 節第 8 條）。
/// </summary>
public static class ResponsiveRules
{
    private const double WideEnter = 1000, WideLeave = 992;
    private const double CompactEnterFromNarrow = 628, CompactEnter = 620, CompactLeave = 612;
    private const double ShortEnter = 700, ShortLeave = 708;
    private const double VeryShortEnter = 600, VeryShortLeave = 608;

    public static WidthClass ClassifyWidth(double width, WidthClass previous)
    {
        var wideLimit = previous == WidthClass.Wide ? WideLeave : WideEnter;
        if (width >= wideLimit)
            return WidthClass.Wide;

        var compactLimit = previous switch
        {
            WidthClass.Narrow => CompactEnterFromNarrow,
            WidthClass.Compact => CompactLeave,
            _ => CompactEnter,
        };
        return width >= compactLimit ? WidthClass.Compact : WidthClass.Narrow;
    }

    public static HeightClass ClassifyHeight(double height, HeightClass previous)
    {
        var veryShortLimit = previous == HeightClass.VeryShort ? VeryShortLeave : VeryShortEnter;
        if (height < veryShortLimit)
            return HeightClass.VeryShort;

        var shortLimit = previous is HeightClass.Short or HeightClass.VeryShort ? ShortLeave : ShortEnter;
        return height < shortLimit ? HeightClass.Short : HeightClass.Normal;
    }
}
