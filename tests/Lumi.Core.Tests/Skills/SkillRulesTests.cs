using Lumi.Core.Skills;

namespace Lumi.Core.Tests.Skills;

/// <summary>M29 5.2：技能的名稱、描述、內文規則與路徑、標籤檢查（純函式）。</summary>
public sealed class SkillRulesTests
{
    [Theory]
    [InlineData("週報")]
    [InlineData("會議 紀錄")]
    [InlineData("report-v2")]
    [InlineData("a.b")]
    [InlineData("CONSOLE")]
    public void ValidateName_AcceptsReasonableNames(string name) =>
        Assert.Null(SkillRules.ValidateName(name));

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("nul.txt")]
    [InlineData("COM1.md")]
    [InlineData("lpt9")]
    public void ValidateName_RejectsWindowsReservedNames(string name) =>
        Assert.NotNull(SkillRules.ValidateName(name));

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\tb")]
    public void ValidateName_RejectsIllegalCharacters(string name) =>
        Assert.NotNull(SkillRules.ValidateName(name));

    [Theory]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData(" 前後空白 ")]
    public void ValidateName_RejectsDotAndSpaceAtTheEdges(string name) =>
        Assert.NotNull(SkillRules.ValidateName(name));

    [Fact]
    public void ValidateName_Empty_IsRejected() =>
        Assert.NotNull(SkillRules.ValidateName(""));

    [Fact]
    public void ValidateName_LengthCountsTextElements()
    {
        var sixtyFour = new string('字', SkillRules.MaxNameLength);
        var sixtyFive = new string('字', SkillRules.MaxNameLength + 1);

        Assert.Null(SkillRules.ValidateName(sixtyFour));
        Assert.NotNull(SkillRules.ValidateName(sixtyFive));
    }

    [Fact]
    public void ValidateDescription_LengthLimits()
    {
        Assert.NotNull(SkillRules.ValidateDescription(""));
        Assert.Null(SkillRules.ValidateDescription(new string('a', SkillRules.MaxDescriptionLength)));
        Assert.NotNull(SkillRules.ValidateDescription(new string('a', SkillRules.MaxDescriptionLength + 1)));
    }

    [Fact]
    public void ValidateBody_LengthLimits()
    {
        Assert.NotNull(SkillRules.ValidateBody(""));
        Assert.Null(SkillRules.ValidateBody(new string('步', SkillRules.MaxBodyLength)));
        Assert.NotNull(SkillRules.ValidateBody(new string('步', SkillRules.MaxBodyLength + 1)));
    }

    [Fact]
    public void Validate_ReportsTheFirstProblem_AfterNormalizing()
    {
        var ok = new SkillDraft("  週報  ", "每週\n五", "  步驟  ");
        Assert.Null(SkillRules.Validate(ok));

        Assert.NotNull(SkillRules.Validate(new SkillDraft("", "d", "b")));
        Assert.NotNull(SkillRules.Validate(new SkillDraft("n", "   ", "b")));
        Assert.NotNull(SkillRules.Validate(new SkillDraft("n", "d", "\n\n")));
    }

    [Fact]
    public void NormalizeDescription_CollapsesWhitespace() =>
        Assert.Equal("第一行 第二行", SkillRules.NormalizeDescription("  第一行\r\n\n第二行  "));

    [Fact]
    public void NormalizeBody_UsesLfAndTrimsEdgeBlankLines() =>
        Assert.Equal("第一行\n\n第二行", SkillRules.NormalizeBody("\r\n第一行\r\n\r\n第二行\r\n\r\n"));

    [Fact]
    public void NormalizeName_TrimsNull_IsEmpty()
    {
        Assert.Equal("", SkillRules.NormalizeName(null));
        Assert.Equal("週報", SkillRules.NormalizeName(" 週報 "));
    }

    [Theory]
    [InlineData("templates/週報.md", "templates/週報.md")]
    [InlineData("a\\b\\c.txt", "a/b/c.txt")]
    [InlineData("  notes.md  ", "notes.md")]
    public void TryNormalizeRelativePath_AcceptsSafePaths(string input, string expected)
    {
        Assert.True(SkillRules.TryNormalizeRelativePath(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:\\secret.txt")]
    [InlineData("C:secret.txt")]
    [InlineData("../SKILL.md")]
    [InlineData("a/../../b.md")]
    [InlineData("a/./b.md")]
    [InlineData("a//b.md")]
    [InlineData("scripts/run.md")]
    [InlineData("Scripts/x.txt")]
    [InlineData("scripts\\x.txt")]
    public void TryNormalizeRelativePath_RejectsUnsafePaths(string? input)
    {
        Assert.False(SkillRules.TryNormalizeRelativePath(input, out _));
    }

    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("DATA.JSON", true)]
    [InlineData("table.CSV", true)]
    [InlineData("page.htm", true)]
    [InlineData("run.py", false)]
    [InlineData("tool.exe", false)]
    [InlineData("photo.png", false)]
    [InlineData("noext", false)]
    public void IsTextFile_OnlyLooksAtTheExtension(string path, bool expected) =>
        Assert.Equal(expected, SkillRules.IsTextFile(path));

    [Fact]
    public void EscapeTags_EscapesSkillTagsOnly()
    {
        Assert.Equal("&lt;lumi_skill name=\"x\">", SkillRules.EscapeTags("<lumi_skill name=\"x\">"));
        Assert.Equal("&lt;/lumi_skill>", SkillRules.EscapeTags("</lumi_skill>"));
        Assert.Equal("&lt;lumi_skills>&lt;/LUMI_SKILLS>", SkillRules.EscapeTags("<lumi_skills></LUMI_SKILLS>"));
        Assert.Equal("<div>保留</div>", SkillRules.EscapeTags("<div>保留</div>"));
    }

    [Fact]
    public void ListCharacters_CountsOnlyUsableSkills()
    {
        var skills = new[]
        {
            new SkillInfo("週報", "描述", true, false, null),
            new SkillInfo("停用", "描述", false, false, null),
            new SkillInfo("壞掉", "描述", true, false, "有問題"),
        };

        Assert.Equal("週報".Length + "描述".Length + 4, SkillRules.ListCharacters(skills));
    }
}
