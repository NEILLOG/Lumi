using Lumi.Core.Skills;

namespace Lumi.Core.Tests.Skills;

/// <summary>M29 5.2：SKILL.md 的 frontmatter 子集解析與寫出。</summary>
public sealed class SkillFrontmatterTests
{
    [Fact]
    public void PlainValues_AreParsed()
    {
        var md = SkillFrontmatter.Parse("---\nname: 週報\ndescription: 每週五寫週報時使用\n---\n\n1. 步驟\n");

        Assert.True(md.HasFrontmatter);
        Assert.Equal("週報", md.Name);
        Assert.Equal("每週五寫週報時使用", md.Description);
        Assert.Equal("1. 步驟", md.Body);
        Assert.Empty(md.OtherLines);
        Assert.Empty(md.OtherKeys);
    }

    [Fact]
    public void PlainValue_KeepsHashAndColonInside()
    {
        var md = SkillFrontmatter.Parse("---\nname: C# 程式\ndescription: 把 a: b 整理成表格 # 不是註解\n---\nx");

        Assert.Equal("C# 程式", md.Name);
        Assert.Equal("把 a: b 整理成表格 # 不是註解", md.Description);
    }

    [Fact]
    public void SingleQuoted_DoubledQuoteIsOneQuote()
    {
        var md = SkillFrontmatter.Parse("---\nname: 'it''s here'\ndescription: '含 \\n 的字面文字'\n---\n內文");

        Assert.Equal("it's here", md.Name);
        Assert.Equal("含 \\n 的字面文字", md.Description);
    }

    [Fact]
    public void DoubleQuoted_EscapesAreDecoded()
    {
        var md = SkillFrontmatter.Parse("---\nname: \"a\\\"b\"\ndescription: \"c\\\\d\\ne\"\n---\n內文");

        Assert.Equal("a\"b", md.Name);
        Assert.Equal("c\\d\ne", md.Description);
    }

    [Fact]
    public void DoubleQuoted_WithoutClosingQuote_IsPlainText()
    {
        var md = SkillFrontmatter.Parse("---\nname: \"沒有結尾\n---\n內文");

        Assert.Equal("\"沒有結尾", md.Name);
    }

    [Theory]
    [InlineData("|")]
    [InlineData("|-")]
    [InlineData("|+")]
    public void LiteralBlock_KeepsLineBreaks(string header)
    {
        var md = SkillFrontmatter.Parse($"---\ndescription: {header}\n  第一行\n    縮排的第二行\n  第三行\n---\n內文");

        Assert.Equal("第一行\n  縮排的第二行\n第三行", md.Description);
    }

    [Theory]
    [InlineData(">")]
    [InlineData(">-")]
    [InlineData(">+")]
    public void FoldedBlock_JoinsLinesWithSpaces_AndEmptyLinesBecomeBreaks(string header)
    {
        var md = SkillFrontmatter.Parse($"---\ndescription: {header}\n  第一行\n  第二行\n\n  第三行\n---\n內文");

        Assert.Equal("第一行 第二行\n第三行", md.Description);
    }

    [Fact]
    public void Block_TrailingEmptyLinesAreNotPartOfTheValue()
    {
        var md = SkillFrontmatter.Parse("---\nname: |-\n  週報\n\n\ndescription: 描述\n---\n內文");

        Assert.Equal("週報", md.Name);
        Assert.Equal("描述", md.Description);
    }

    [Fact]
    public void UnknownKeys_AreKeptWithTheirChildLines()
    {
        const string text = """
            ---
            name: 會議紀錄
            description: 整理會議紀錄
            allowed-tools: Read Write
            metadata:
              author: 張三
              tags:
                - 會議
            model: haiku
            ---
            內文
            """;

        var md = SkillFrontmatter.Parse(text);

        Assert.Equal(["allowed-tools", "metadata", "model"], md.OtherKeys);
        Assert.Equal(
            ["allowed-tools: Read Write", "metadata:", "  author: 張三", "  tags:", "    - 會議", "model: haiku"],
            md.OtherLines);
        Assert.Equal("內文", md.Body);
    }

    [Fact]
    public void Lines_WithoutAKeyBeforeIt_AreIgnored()
    {
        var md = SkillFrontmatter.Parse("---\n  孤立的行\nname: 週報\ndescription: d\n---\n內文");

        Assert.Equal("週報", md.Name);
        Assert.Empty(md.OtherLines);
    }

    [Fact]
    public void FirstLineNotFence_MeansNoFrontmatter_WholeTextIsBody()
    {
        var md = SkillFrontmatter.Parse("name: 不是 frontmatter\n---\n步驟");

        Assert.False(md.HasFrontmatter);
        Assert.Null(md.Name);
        Assert.Null(md.Description);
        Assert.Equal("name: 不是 frontmatter\n---\n步驟", md.Body);
    }

    [Fact]
    public void MissingClosingFence_MeansNoFrontmatter()
    {
        var md = SkillFrontmatter.Parse("---\nname: 週報\ndescription: d\n內文沒有結尾");

        Assert.False(md.HasFrontmatter);
        Assert.Null(md.Name);
        Assert.StartsWith("---\nname", md.Body);
    }

    [Fact]
    public void MissingDescription_IsNull()
    {
        var md = SkillFrontmatter.Parse("---\nname: 週報\n---\n內文");

        Assert.True(md.HasFrontmatter);
        Assert.Equal("週報", md.Name);
        Assert.Null(md.Description);
    }

    [Fact]
    public void DuplicateName_UsesTheFirstOne()
    {
        var md = SkillFrontmatter.Parse("---\nname: 第一個\nname: 第二個\ndescription: d\n---\n內文");

        Assert.Equal("第一個", md.Name);
        Assert.Empty(md.OtherLines);
    }

    [Fact]
    public void Bom_IsIgnored()
    {
        var md = SkillFrontmatter.Parse("\uFEFF---\nname: 週報\ndescription: d\n---\n內文");

        Assert.True(md.HasFrontmatter);
        Assert.Equal("週報", md.Name);
    }

    [Fact]
    public void CrLf_IsAccepted()
    {
        var md = SkillFrontmatter.Parse("---\r\nname: 週報\r\ndescription: d\r\n---\r\n第一行\r\n第二行\r\n");

        Assert.Equal("週報", md.Name);
        Assert.Equal("d", md.Description);
        Assert.Equal("第一行\n第二行", md.Body);
    }

    [Fact]
    public void Body_IsNormalized()
    {
        var md = SkillFrontmatter.Parse("---\nname: x\ndescription: d\n---\n\n  \n步驟\n\n");

        Assert.Equal("步驟", md.Body);
    }

    [Fact]
    public void Write_ProducesTheDocumentedLayout()
    {
        var text = SkillFrontmatter.Write("週報", "每週五寫", "1. 整理");

        Assert.Equal("---\nname: \"週報\"\ndescription: \"每週五寫\"\n---\n\n1. 整理\n", text);
    }

    [Fact]
    public void Write_EscapesBackslashAndQuote()
    {
        var text = SkillFrontmatter.Write("a\"b", "路徑 C:\\tmp 與 \"引號\"", "內文");

        Assert.Contains("name: \"a\\\"b\"", text);
        Assert.Contains("description: \"路徑 C:\\\\tmp 與 \\\"引號\\\"\"", text);
    }

    [Fact]
    public void Write_NormalizesDescriptionToOneLine()
    {
        var text = SkillFrontmatter.Write("週報", "第一行\n\n第二行", "內文");

        Assert.Contains("description: \"第一行 第二行\"", text);
    }

    [Fact]
    public void Write_OtherLinesAreWrittenVerbatim()
    {
        var other = new[] { "allowed-tools: Read", "metadata:", "  author: 張三" };

        var text = SkillFrontmatter.Write("週報", "d", "內文", other);

        Assert.Contains("description: \"d\"\nallowed-tools: Read\nmetadata:\n  author: 張三\n---\n", text);
    }

    [Theory]
    [InlineData("週報", "每週五整理本週工作", "1. 先問\n2. 整理")]
    [InlineData("a\"b\\c", "含 \"引號\" 與 \\反斜線", "步驟")]
    [InlineData("會議", "描述", "")]
    public void Write_ThenParse_RoundTrips(string name, string description, string body)
    {
        var other = new[] { "allowed-tools: Read Write", "metadata:", "  author: 張三", "  tags:", "    - 會議" };

        var md = SkillFrontmatter.Parse(SkillFrontmatter.Write(name, description, body, other));

        Assert.True(md.HasFrontmatter);
        Assert.Equal(name, md.Name);
        Assert.Equal(SkillRules.NormalizeDescription(description), md.Description);
        Assert.Equal(SkillRules.NormalizeBody(body), md.Body);
        Assert.Equal(other, md.OtherLines);
        Assert.Equal(["allowed-tools", "metadata"], md.OtherKeys);
    }

    [Fact]
    public void Write_ThenParse_WithoutOtherLines_RoundTrips()
    {
        var md = SkillFrontmatter.Parse(SkillFrontmatter.Write("週報", "d", "第一行\n\n第二行"));

        Assert.Equal("週報", md.Name);
        Assert.Equal("d", md.Description);
        Assert.Equal("第一行\n\n第二行", md.Body);
        Assert.Empty(md.OtherLines);
    }

    [Fact]
    public void Parse_OfWrittenBlockStyleFile_ReadsBack()
    {
        // 別人寫的 SKILL.md 可能用區塊字串；寫回後再讀要一致
        var original = SkillFrontmatter.Parse("---\nname: 週報\ndescription: >-\n  每週五\n  整理本週工作\nmodel: haiku\n---\n步驟");

        var again = SkillFrontmatter.Parse(SkillFrontmatter.Write(original.Name!, original.Description!, original.Body, original.OtherLines));

        Assert.Equal("每週五 整理本週工作", again.Description);
        Assert.Equal(original.OtherLines, again.OtherLines);
    }
}
