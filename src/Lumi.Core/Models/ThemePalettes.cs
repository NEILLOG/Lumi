namespace Lumi.Core.Models;

/// <summary>
/// 主題色票的 ID（ui-guide.html 第 1 節）。色碼在 Lumi.App/Themes/Palettes，這裡只放 ID，讓設定與測試不需要 Avalonia。
/// 只存 ID，不存個別顏色（原則規定）。
/// </summary>
public static class ThemePalettes
{
    public const string Default = "sage";

    public static IReadOnlyList<string> All { get; } = ["sage", "teal", "mist-blue", "indigo", "terracotta"];

    /// <summary>缺少或不認得的 ID 一律回到預設。舊設定檔沒有這個欄位時也是預設。</summary>
    public static string Normalize(string? id) => id is not null && All.Contains(id, StringComparer.Ordinal) ? id : Default;
}
