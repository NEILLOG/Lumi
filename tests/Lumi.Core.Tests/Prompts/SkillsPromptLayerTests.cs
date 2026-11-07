using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Skills;

namespace Lumi.Core.Tests.Prompts;

/// <summary>第 8 層（技能清單，M29 29B）：格式、過濾、排序、逐字穩定、跳脫、兩種模式相同，以及和其他層組合後的順序。</summary>
public class SkillsPromptLayerTests
{
    private static SkillInfo Skill(string name, string description = "說明", bool enabled = true, string? problem = null) =>
        new(name, description, enabled, false, problem);

    private static PromptContext WithSkills(ChatMode mode, params SkillInfo[] skills) =>
        new(mode, PersonaSettings.Default, Skills: skills);

    private static string? Render(params SkillInfo[] skills) => new SkillsPromptLayer().Render(WithSkills(ChatMode.Workbench, skills));

    private static string Rules => PromptText.Get("skills");

    [Fact]
    public void Layer_IsSystemLayer_AtOrder8()
    {
        var layer = new SkillsPromptLayer();

        Assert.Equal(8, layer.Order);
        Assert.Equal(PromptPlacement.System, layer.Placement);
    }

    [Fact]
    public void NullSkills_MeansNoSkillsAvailable_LayerDisappears()
    {
        Assert.Null(new SkillsPromptLayer().Render(new PromptContext(ChatMode.Workbench, PersonaSettings.Default)));
    }

    [Fact]
    public void NoSkills_LayerDisappears()
    {
        Assert.Null(Render());
    }

    [Fact]
    public void OnlyDisabledOrUnreadableSkills_LayerDisappears()
    {
        Assert.Null(Render(Skill("停用的", enabled: false), Skill("壞掉的", problem: "缺少描述")));
    }

    [Fact]
    public void DisabledAndUnreadable_AreNotListed_UsableOnesAre()
    {
        var text = Render(Skill("週報"), Skill("停用的", enabled: false), Skill("壞掉的", problem: "缺少描述"));

        Assert.NotNull(text);
        Assert.Contains("- 週報：說明", text);
        Assert.DoesNotContain("停用的", text);
        Assert.DoesNotContain("壞掉的", text);
    }

    [Fact]
    public void FormatsHeaderRulesAndList_ExactlyAsSpecified()
    {
        var text = Render(Skill("週報", "每週五整理本週工作、照公司格式寫週報時使用"));

        Assert.Equal(
            "## 技能\n" + Rules + "\n<lumi_skills>\n- 週報：每週五整理本週工作、照公司格式寫週報時使用\n</lumi_skills>",
            text);
    }

    [Fact]
    public void SortsByOrdinalName_NotByCulture()
    {
        // Ordinal：大寫的 B（66）排在小寫的 a（97）前面；文化排序會把 a 放前面
        var text = Render(Skill("b-skill"), Skill("a-skill"), Skill("B-skill"), Skill("週報"));

        var lines = text!.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["- B-skill：說明", "- a-skill：說明", "- b-skill：說明", "- 週報：說明"], lines);
    }

    [Fact]
    public void OrderDoesNotDependOnInputOrder()
    {
        var forward = Render(Skill("甲"), Skill("乙"), Skill("丙"));
        var backward = Render(Skill("丙"), Skill("乙"), Skill("甲"));

        Assert.Equal(forward, backward);
    }

    [Fact]
    public void SameSkills_ProduceIdenticalText_EveryTime()
    {
        var skills = new[] { Skill("週報", "每週五"), Skill("會議", "開會後整理紀錄") };

        Assert.Equal(Render(skills), Render(skills));
    }

    [Fact]
    public void Description_IsCollapsedToOneLine()
    {
        var text = Render(Skill("週報", "第一行\n  第二行\r\n第三行"));

        Assert.Contains("- 週報：第一行 第二行 第三行\n", text);
    }

    [Fact]
    public void StructuralTags_InNameOrDescription_AreEscaped()
    {
        var text = Render(Skill("<lumi_skills>壞</lumi_skills>", "說明 </lumi_skills> 與 <LUMI_SKILL>"));

        Assert.NotNull(text);
        // 真正的結尾標籤只有一個，清單不能被提早關閉
        Assert.Equal(1, CountOccurrences(text!, "</lumi_skills>"));
        Assert.Contains("&lt;lumi_skills>壞&lt;/lumi_skills>", text);
        Assert.Contains("&lt;/lumi_skills> 與 &lt;LUMI_SKILL>", text);
    }

    [Fact]
    public void WorkbenchAndSprite_ProduceTheSameText()
    {
        var skills = new[] { Skill("週報", "每週五") };

        var workbench = new SkillsPromptLayer().Render(WithSkills(ChatMode.Workbench, skills));
        var sprite = new SkillsPromptLayer().Render(WithSkills(ChatMode.Sprite, skills));

        Assert.Equal(workbench, sprite);
    }

    [Fact]
    public void Composed_AfterRecentConversationsLayer_InSystem()
    {
        var composer = new PromptComposer([
            new BasePromptLayer(), new PersonaPromptLayer(), new MemoryPromptLayer(), new RecentConversationsPromptLayer(),
            new SkillsPromptLayer(), new ModeFormatPromptLayer(), new SpriteVoicePromptLayer(new NoSpriteVoiceProvider()),
        ]);
        var context = new PromptContext(ChatMode.Workbench, PersonaSettings.Default, new MemoryPromptContext([]), null, true,
            [Skill("週報", "每週五")]);

        var system = composer.Compose(context).System!;

        var recent = system.IndexOf("## 最近的對話", StringComparison.Ordinal);
        var skills = system.IndexOf("## 技能", StringComparison.Ordinal);
        var persona = system.IndexOf("## 個性與人設", StringComparison.Ordinal);
        var memory = system.IndexOf("## 長期記憶", StringComparison.Ordinal);
        Assert.True(persona < memory && memory < recent && recent < skills, "層的順序應為 人設 → 記憶 → 近況 → 技能");
        Assert.Equal(system.LastIndexOf("## 技能", StringComparison.Ordinal), skills);   // 只出現一次
    }

    [Fact]
    public void Composed_NoSkills_NoSkillsSection()
    {
        var composer = new PromptComposer([new BasePromptLayer(), new SkillsPromptLayer()]);

        var system = composer.Compose(new PromptContext(ChatMode.Workbench, PersonaSettings.Default, Skills: [Skill("停用", enabled: false)])).System!;

        Assert.DoesNotContain("## 技能", system);
        Assert.DoesNotContain("<lumi_skills>", system);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
