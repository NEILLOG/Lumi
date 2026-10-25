namespace Lumi.Core.Models;

/// <summary>
/// 工具清單頁的設定（M15）。<paramref name="Disabled"/>＝使用者關閉的工具名稱（新對話才生效）；
/// <paramref name="AlwaysAllow"/>＝設為「永遠允許」的工具名稱（立即生效，只對 Write／Network 有效）。
/// 兩份清單都以序數排序、去重，這樣設定檔的內容穩定。
/// </summary>
public sealed record ToolSettings
{
    public static readonly ToolSettings Default = new();

    public IReadOnlyList<string> Disabled { get; init; } = [];

    public IReadOnlyList<string> AlwaysAllow { get; init; } = [];

    public ToolSettings Normalize() => this with
    {
        Disabled = Clean(Disabled),
        AlwaysAllow = Clean(AlwaysAllow),
    };

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? names) =>
        (names ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
