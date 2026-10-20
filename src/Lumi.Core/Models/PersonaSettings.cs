namespace Lumi.Core.Models;

/// <summary>個性（M5 的 Q3）。每一種對應 <c>Prompts/persona-style-*.md</c> 的一段文字。</summary>
public enum PersonaStyle { Warm, Professional, Playful, Calm }

/// <summary>積極度：只影響回答時要不要主動補充建議或提問，<b>不影響工具權限確認</b>。</summary>
public enum Proactivity { Reserved, Balanced, Proactive }

/// <summary>
/// 人設設定，組成 system 的第 2 層（agent.md 第 9 節）。改動它會改到 system，下一則訊息的快取會失效一次（可以接受，是使用者的動作）。
/// </summary>
/// <param name="CustomPersona">使用者自訂的補充，最多 <see cref="CustomPersonaMaxLength"/> 字；空白視為沒有。</param>
public sealed record PersonaSettings(PersonaStyle Style, Proactivity Proactivity, string? CustomPersona)
{
    public const int CustomPersonaMaxLength = 2000;

    public static readonly PersonaSettings Default = new(PersonaStyle.Warm, Proactivity.Balanced, null);

    /// <summary>去掉前後空白，空字串變成 null；不認得的列舉值改回預設（手改設定檔時）。</summary>
    public PersonaSettings Normalize() => new(
        Enum.IsDefined(Style) ? Style : Default.Style,
        Enum.IsDefined(Proactivity) ? Proactivity : Default.Proactivity,
        string.IsNullOrWhiteSpace(CustomPersona) ? null : CustomPersona.Trim());
}
