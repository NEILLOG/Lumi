using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.Core.Prompts;

/// <summary>
/// 一層 prompt 放在哪裡（agent.md 第 9 節）。<see cref="TurnSuffix"/>＝請求的最尾端（最新一則使用者訊息或工具結果之後），
/// 只存在請求裡、不存檔，也不在快取斷點之內，所以切換模式不會讓快取失效（M5 的 Q2）。
/// </summary>
public enum PromptPlacement { System, TurnSuffix }

/// <summary>組 prompt 時看得到的資訊。不帶對話內容：層只依設定與模式產生文字。</summary>
public sealed record PromptContext(ChatMode Mode, PersonaSettings Persona);

public interface IPromptLayer
{
    /// <summary>1 基底、2 人設、3 模式格式、4 角色語氣。不能重複。</summary>
    int Order { get; }

    PromptPlacement Placement { get; }

    /// <summary>null 或空白＝這一層這次不出現。<see cref="PromptPlacement.System"/> 的層<b>不能</b>依 <see cref="PromptContext.Mode"/> 改變內容。</summary>
    string? Render(PromptContext context);
}

/// <param name="System">送成 system prompt；沒有任何內容時是 null。</param>
/// <param name="TurnSuffix">附在請求最尾端，已經包好 <c>&lt;lumi_mode&gt;</c>；沒有內容時是 null。</param>
public sealed record ComposedPrompt(string? System, string? TurnSuffix)
{
    public static readonly ComposedPrompt Empty = new(null, null);
}

public interface IPromptComposer
{
    ComposedPrompt Compose(PromptContext context);
}

/// <summary>小精靈的角色語氣（第 4 層）。M7 由目前的角色提供；之前一律沒有。</summary>
public interface ISpriteVoiceProvider
{
    /// <summary>目前角色的語氣描述；沒有時回 null。</summary>
    string? GetVoice();
}

public sealed class NoSpriteVoiceProvider : ISpriteVoiceProvider
{
    public string? GetVoice() => null;
}

public sealed class PromptOptions
{
    /// <summary>小精靈模式的 <c>max_tokens</c> 上限（和模型設定取小的）。思考也算在裡面，太低會在思考途中被截斷（M5 的 Q4）。</summary>
    public int SpriteMaxOutputTokens { get; set; } = 2048;
}

/// <summary>不組任何 prompt（測試與不需要 prompt 的場合）。</summary>
public sealed class NullPromptComposer : IPromptComposer
{
    public ComposedPrompt Compose(PromptContext context) => ComposedPrompt.Empty;
}
