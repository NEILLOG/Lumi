using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>自訂標題列的平台排列。Linux 照 Windows 的排列。</summary>
public enum TitleBarPlatform { Windows, MacOS }

/// <summary>
/// 標題列的排列（純函式，不碰視窗，可以測試）。
/// Windows：系統按鈕在右（由系統繪製，疊在標題列上），小精靈按鈕緊鄰其左，中間有分隔線。
/// macOS：紅綠燈在左，品牌往右讓出，小精靈按鈕放最右。
/// </summary>
public sealed record TitleBarLayout(double LeftInset, double RightInset, bool ShowDivider)
{
    /// <summary>標題列高度（DIP）。</summary>
    public const double Height = 40;

    /// <summary>macOS 紅綠燈佔用的寬度（含與品牌的間距）。</summary>
    public const double MacTrafficLightsWidth = 80;

    /// <summary>Windows 系統按鈕（最小化、最大化、關閉各 46）加上與分隔線的間距 8。</summary>
    public const double WindowsCaptionButtonsWidth = 138 + 8;

    private const double Gutter = 16;

    public static TitleBarPlatform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? TitleBarPlatform.MacOS : TitleBarPlatform.Windows;

    public static TitleBarLayout For(TitleBarPlatform platform) => platform switch
    {
        TitleBarPlatform.MacOS => new TitleBarLayout(MacTrafficLightsWidth, Gutter, ShowDivider: false),
        _ => new TitleBarLayout(Gutter, WindowsCaptionButtonsWidth, ShowDivider: true),
    };

    /// <summary>雙擊標題列空白處：可調整大小的視窗在最大化與一般之間切換；其他情況不動。</summary>
    public static WindowState NextStateOnDoubleClick(WindowState current, bool canResize) =>
        !canResize ? current : current == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
