using Lumi.Core.Tools;

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

    /// <summary>使用者調整過權限等級的工具（只對 MCP 工具有效，M16 D3）。鍵是工具名稱。</summary>
    public IReadOnlyDictionary<string, ToolPermission> PermissionOverrides { get; init; } = new Dictionary<string, ToolPermission>();

    public ToolSettings Normalize() => this with
    {
        Disabled = Clean(Disabled),
        AlwaysAllow = Clean(AlwaysAllow),
        PermissionOverrides = (PermissionOverrides ?? new Dictionary<string, ToolPermission>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Key) && Enum.IsDefined(p.Value))
            .OrderBy(p => p.Key.Trim(), StringComparer.Ordinal)
            .GroupBy(p => p.Key.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal),
    };

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? names) =>
        (names ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
