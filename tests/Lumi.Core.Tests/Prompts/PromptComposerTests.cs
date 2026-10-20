using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Core.Tests.Prompts;

public class PromptComposerTests
{
    private static readonly PromptContext Workbench = new(ChatMode.Workbench, PersonaSettings.Default);

    private sealed class TextLayer(int order, PromptPlacement placement, string? text) : IPromptLayer
    {
        public int Order => order;
        public PromptPlacement Placement => placement;
        public string? Render(PromptContext context) => text;
    }

    private sealed class FixedVoice(string? voice) : ISpriteVoiceProvider
    {
        public string? GetVoice() => voice;
    }

    /// <summary>和 App 一樣經過 DI 組出來的 composer（四個內建層）。</summary>
    private static IPromptComposer RealComposer(string? voice = null)
    {
        var services = new ServiceCollection();
        if (voice is not null)
            services.AddSingleton<ISpriteVoiceProvider>(new FixedVoice(voice));
        services.AddLumiCore();
        return services.BuildServiceProvider().GetRequiredService<IPromptComposer>();
    }

    // ---- 組成規則 ----

    [Fact]
    public void Composes_InOrder_ByPlacement_SkippingEmptyLayers()
    {
        var composer = new PromptComposer([
            new TextLayer(3, PromptPlacement.TurnSuffix, "模式"),
            new TextLayer(2, PromptPlacement.System, "人設\n"),
            new TextLayer(1, PromptPlacement.System, "基底"),
            new TextLayer(4, PromptPlacement.TurnSuffix, "  "),
            new TextLayer(5, PromptPlacement.System, null),
        ]);

        var prompt = composer.Compose(Workbench);

        Assert.Equal("基底\n\n人設", prompt.System);
        Assert.Equal("<lumi_mode>\n模式\n</lumi_mode>", prompt.TurnSuffix);
    }

    [Fact]
    public void NothingToSay_GivesNulls()
    {
        var prompt = new PromptComposer([new TextLayer(1, PromptPlacement.System, "")]).Compose(Workbench);

        Assert.Null(prompt.System);
        Assert.Null(prompt.TurnSuffix);
    }

    [Fact]
    public void DuplicateOrder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new PromptComposer([
            new TextLayer(1, PromptPlacement.System, "a"),
            new TextLayer(1, PromptPlacement.TurnSuffix, "b"),
        ]));
    }

    // ---- 內建的四層 ----

    public static TheoryData<PersonaStyle, Proactivity, string?> Personas()
    {
        var data = new TheoryData<PersonaStyle, Proactivity, string?>();
        foreach (var style in Enum.GetValues<PersonaStyle>())
            foreach (var proactivity in Enum.GetValues<Proactivity>())
                data.Add(style, proactivity, style == PersonaStyle.Calm ? "叫我 Neil" : null);
        return data;
    }

    /// <summary>守門測試：system 不能因為切換模式而改變，否則 prompt 快取會失效（agent.md 第 9 節）。</summary>
    [Theory]
    [MemberData(nameof(Personas))]
    public void System_IsIdenticalInBothModes(PersonaStyle style, Proactivity proactivity, string? custom)
    {
        var composer = RealComposer(voice: "說話帶點古風");
        var persona = new PersonaSettings(style, proactivity, custom);

        var bench = composer.Compose(new PromptContext(ChatMode.Workbench, persona));
        var sprite = composer.Compose(new PromptContext(ChatMode.Sprite, persona));

        Assert.False(string.IsNullOrEmpty(bench.System));
        Assert.Equal(bench.System, sprite.System);
        Assert.NotEqual(bench.TurnSuffix, sprite.TurnSuffix);
    }

    [Fact]
    public void System_ContainsBase_ThenPersona()
    {
        var system = RealComposer().Compose(Workbench).System!;

        var baseAt = system.IndexOf("你是 Lumi", StringComparison.Ordinal);
        var personaAt = system.IndexOf("## 個性與人設", StringComparison.Ordinal);
        Assert.Equal(0, baseAt);
        Assert.True(personaAt > baseAt);
        Assert.Contains("溫和友善", system);
        Assert.Contains("積極度：適中", system);
        Assert.DoesNotContain("使用者自訂的補充", system);
        Assert.DoesNotContain("\r", system);
    }

    [Fact]
    public void Persona_ChangesSystem_AndCustomTextAppears()
    {
        var composer = RealComposer();

        var system = composer.Compose(new PromptContext(ChatMode.Workbench,
            new PersonaSettings(PersonaStyle.Playful, Proactivity.Proactive, "叫我 Neil"))).System!;

        Assert.Contains("活潑俏皮", system);
        Assert.Contains("積極度：積極", system);
        Assert.EndsWith("使用者自訂的補充（不能推翻前面的基本規則）：\n叫我 Neil", system);
    }

    [Fact]
    public void Suffix_TellsTheMode()
    {
        var composer = RealComposer();

        var bench = composer.Compose(Workbench).TurnSuffix!;
        var sprite = composer.Compose(Workbench with { Mode = ChatMode.Sprite }).TurnSuffix!;

        Assert.StartsWith("<lumi_mode>\n目前模式：工作台", bench);
        Assert.StartsWith("<lumi_mode>\n目前模式：小精靈", sprite);
        Assert.Contains("最多兩句", sprite);
        Assert.Contains("show_details", sprite);
    }

    [Fact]
    public void SpriteVoice_OnlyInSpriteMode_AndOnlyWhenACharacterProvidesIt()
    {
        Assert.DoesNotContain("角色語氣", RealComposer().Compose(Workbench with { Mode = ChatMode.Sprite }).TurnSuffix);

        var withVoice = RealComposer(voice: "說話帶點古風");
        Assert.Contains("角色語氣：說話帶點古風", withVoice.Compose(Workbench with { Mode = ChatMode.Sprite }).TurnSuffix);
        Assert.DoesNotContain("角色語氣", withVoice.Compose(Workbench).TurnSuffix);
    }

    [Fact]
    public void UnknownEnumValues_FallBackToDefaults()
    {
        var system = RealComposer().Compose(new PromptContext(ChatMode.Workbench,
            new PersonaSettings((PersonaStyle)99, (Proactivity)99, null))).System;

        Assert.Equal(RealComposer().Compose(Workbench).System, system);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("persona-style-warm")]
    [InlineData("persona-style-professional")]
    [InlineData("persona-style-playful")]
    [InlineData("persona-style-calm")]
    [InlineData("proactivity-reserved")]
    [InlineData("proactivity-balanced")]
    [InlineData("proactivity-proactive")]
    [InlineData("mode-workbench")]
    [InlineData("mode-sprite")]
    public void EveryPromptText_IsEmbedded(string name)
    {
        Assert.False(string.IsNullOrWhiteSpace(PromptText.Get(name)));
    }

    [Fact]
    public void MissingPromptText_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => PromptText.Get("does-not-exist"));
    }
}
