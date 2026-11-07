namespace Lumi.Core.Skills;

/// <summary>
/// 解析後的 SKILL.md（M29 5.2）。<paramref name="OtherLines"/> 是 frontmatter 裡 <c>name</c>、<c>description</c> 以外的原始行
/// （含縮排的子項），寫回時原樣保留；<paramref name="OtherKeys"/> 是其中頂層鍵的名稱（匯入時判斷要不要警告，例如 <c>allowed-tools</c>）。
/// </summary>
public sealed record SkillMarkdown(
    bool HasFrontmatter,
    string? Name,
    string? Description,
    string Body,
    IReadOnlyList<string> OtherLines,
    IReadOnlyList<string> OtherKeys);

/// <summary>
/// SKILL.md 開頭 frontmatter 的極小子集解析與寫出（純函式，沒有 YAML 套件）。
/// 只支援頂層 <c>key: value</c>：純文字、單引號、雙引號，以及 <c>|</c>、<c>&gt;</c> 區塊；縮排的子項當成上一個鍵的原始文字。
/// 第一行不是 <c>---</c> 或找不到結尾的 <c>---</c>：當作沒有 frontmatter，整份都是內文。
/// </summary>
public static class SkillFrontmatter
{
    /// <summary>可以接受 <c>\r\n</c>；回傳的內文已經用 <see cref="SkillRules.NormalizeBody"/> 正規化。</summary>
    public static SkillMarkdown Parse(string text) =>
        throw new NotImplementedException("TODO(29A)");

    /// <summary>
    /// 寫出 SKILL.md：<c>name</c>、<c>description</c> 一律用雙引號並跳脫（<c>\</c>、<c>"</c>），接著原樣寫出 <paramref name="otherLines"/>，
    /// 然後空一行接內文，結尾一個換行。換行一律 <c>\n</c>。
    /// </summary>
    public static string Write(string name, string description, string body, IReadOnlyList<string>? otherLines = null) =>
        throw new NotImplementedException("TODO(29A)");
}
