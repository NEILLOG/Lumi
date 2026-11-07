namespace Lumi.Core.Skills;

/// <summary>
/// 技能的設定（M29 D1）：停用的技能名稱。沒有列在這裡的技能就是啟用，所以新增或自己放進資料夾的技能預設啟用。
/// 名稱不分大小寫；舊設定檔沒有這欄時是空清單。
/// </summary>
public sealed record SkillSettings
{
    public static readonly SkillSettings Default = new();

    public IReadOnlyList<string> Disabled { get; init; } = [];

    public bool IsDisabled(string name) => Disabled.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>去空白、去重複（不分大小寫）、依 Ordinal 排序，存檔內容才穩定。</summary>
    public SkillSettings Normalize() => this with
    {
        Disabled = (Disabled ?? [])
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList(),
    };

    public SkillSettings WithEnabled(string name, bool enabled)
    {
        var rest = Disabled.Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return (this with { Disabled = enabled ? rest.ToList() : [.. rest, name] }).Normalize();
    }

    /// <summary>改名時停用狀態跟著搬過去。</summary>
    public SkillSettings WithRenamed(string oldName, string newName) =>
        IsDisabled(oldName) ? WithEnabled(oldName, true).WithEnabled(newName, false) : this;
}
