namespace Lumi.Core.Models;

/// <summary>
/// 工作台視窗的位置與大小（M9 9D）。
/// <list type="bullet">
/// <item><paramref name="X"/>、<paramref name="Y"/>：視窗左上角，螢幕像素（虛擬桌面座標，多螢幕時可以是負值）。</item>
/// <item><paramref name="Width"/>、<paramref name="Height"/>：視窗用戶端大小，邏輯像素（DIP，與 Avalonia 的 Width／Height 相同）。</item>
/// <item><paramref name="Maximized"/>：是否最大化。位置與大小一律是「還原後」的值，不是最大化的尺寸。</item>
/// <item><paramref name="Scaling"/>：存檔當下視窗所在螢幕的縮放比例（1.0、1.25、2.0…），還原時用來換算成螢幕像素。</item>
/// </list>
/// </summary>
public sealed record WindowSettings(int X, int Y, double Width, double Height, bool Maximized, double Scaling)
{
    /// <summary>大小的上限（DIP）：超過就不是合理的視窗，Normalize 會把它改成這個值，之後由還原規則判定為不可用。</summary>
    public const double MaxSize = 10_000;

    /// <summary>座標的合理範圍（螢幕像素）：超過就夾回來，避免溢位；是否看得到由還原規則判斷。</summary>
    public const int MaxCoordinate = 100_000;

    /// <summary>
    /// 讀進來的值不可信（手動改檔、其他版本寫的）：NaN、無限大、負數的尺寸與縮放比例都不能用。
    /// 寬高不合理時改成 0（還原規則會因為小於最小尺寸而退回預設）；縮放不合理時改成 0（還原規則退回預設，
    /// 因為沒有縮放比例就無法換算）。
    /// </summary>
    public WindowSettings Normalize() => new(
        Math.Clamp(X, -MaxCoordinate, MaxCoordinate),
        Math.Clamp(Y, -MaxCoordinate, MaxCoordinate),
        SanitizeSize(Width),
        SanitizeSize(Height),
        Maximized,
        SanitizeScaling(Scaling));

    private static double SanitizeSize(double value) =>
        double.IsFinite(value) && value > 0 ? Math.Min(value, MaxSize) : 0;

    private static double SanitizeScaling(double value) =>
        double.IsFinite(value) && value > 0 && value <= 8 ? value : 0;
}
