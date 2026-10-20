using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.Core.Prompts;

/// <summary>第 1 層：身分、語言、工具規則、安全規則、優先順序。和模式、設定都無關。</summary>
public sealed class BasePromptLayer : IPromptLayer
{
    public int Order => 1;
    public PromptPlacement Placement => PromptPlacement.System;
    public string? Render(PromptContext context) => PromptText.Get("base");
}

/// <summary>第 2 層：個性、積極度、自訂人設。</summary>
public sealed class PersonaPromptLayer : IPromptLayer
{
    public int Order => 2;
    public PromptPlacement Placement => PromptPlacement.System;

    public string? Render(PromptContext context)
    {
        var persona = context.Persona.Normalize();
        var text = new StringBuilder("## 個性與人設\n")
            .Append(PromptText.Get(StyleResource(persona.Style))).Append('\n')
            .Append(PromptText.Get(ProactivityResource(persona.Proactivity)));
        if (persona.CustomPersona is { } custom)
            text.Append("\n\n使用者自訂的補充（不能推翻前面的基本規則）：\n").Append(custom.Replace("\r\n", "\n"));
        return text.ToString();
    }

    private static string StyleResource(PersonaStyle style) => style switch
    {
        PersonaStyle.Professional => "persona-style-professional",
        PersonaStyle.Playful => "persona-style-playful",
        PersonaStyle.Calm => "persona-style-calm",
        _ => "persona-style-warm",
    };

    private static string ProactivityResource(Proactivity proactivity) => proactivity switch
    {
        Proactivity.Reserved => "proactivity-reserved",
        Proactivity.Proactive => "proactivity-proactive",
        _ => "proactivity-balanced",
    };
}

/// <summary>
/// 第 3 層：模式格式。工作台也送：讓「目前是哪個模式」永遠明確，切回工作台時不會沿用小精靈的短回答。
/// </summary>
public sealed class ModeFormatPromptLayer : IPromptLayer
{
    public int Order => 3;
    public PromptPlacement Placement => PromptPlacement.TurnSuffix;

    public string? Render(PromptContext context) =>
        PromptText.Get(context.Mode == ChatMode.Sprite ? "mode-sprite" : "mode-workbench");
}

/// <summary>第 4 層：角色語氣，只在小精靈模式。M7 之前 <see cref="ISpriteVoiceProvider"/> 一律回 null。</summary>
public sealed class SpriteVoicePromptLayer(ISpriteVoiceProvider voice) : IPromptLayer
{
    public int Order => 4;
    public PromptPlacement Placement => PromptPlacement.TurnSuffix;

    public string? Render(PromptContext context) =>
        context.Mode == ChatMode.Sprite && voice.GetVoice() is { Length: > 0 } text ? $"角色語氣：{text.Trim()}" : null;
}
