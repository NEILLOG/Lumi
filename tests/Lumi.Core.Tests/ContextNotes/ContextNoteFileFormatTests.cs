using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35B：<see cref="ContextNoteFileFormat"/>（note.md 的開頭三行與內容）。</summary>
public class ContextNoteFileFormatTests
{
    private static readonly string Content = ContextNoteDocument.Empty
        .WithSection(ContextNoteDocument.Breakdown, [new NoteBlock("- 拆解一"), new NoteBlock("段落")])
        .ToMarkdown();

    [Fact]
    public void Compose_ThenParse_GivesBackGoalAndContent()
    {
        var text = ContextNoteFileFormat.Compose("對話標題", "我要做的事", Content);

        Assert.Equal("# 對話標題\n\n**目標**：我要做的事\n\n" + Content, text);
        Assert.Equal(("我要做的事", Content), ContextNoteFileFormat.Parse(text, "備用"));
    }

    [Fact]
    public void Parse_GoalLineDeleted_UsesFallback()
    {
        var text = "# 對話標題\n\n" + Content;

        Assert.Equal(("備用目標", Content), ContextNoteFileFormat.Parse(text, "備用目標"));
    }

    [Fact]
    public void Parse_GoalChangedByUser_UsesTheNewGoal()
    {
        var text = ContextNoteFileFormat.Compose("對話標題", "舊目標", Content).Replace("**目標**：舊目標", "**目標**：新目標", StringComparison.Ordinal);

        Assert.Equal(("新目標", Content), ContextNoteFileFormat.Parse(text, "備用"));
    }

    [Theory]
    [InlineData("**目標**：   ")]
    [InlineData("**目標**：")]
    public void Parse_BlankGoal_UsesFallback(string goalLine)
    {
        var text = $"# 對話標題\n\n{goalLine}\n\n{Content}";

        Assert.Equal(("備用", Content), ContextNoteFileFormat.Parse(text, "備用"));
    }

    [Fact]
    public void Parse_GoalOver200Characters_UsesFallback_ExactlyAtLimitIsKept()
    {
        var tooLong = new string('字', ContextNoteLimits.GoalMaxLength + 1);
        var atLimit = new string('字', ContextNoteLimits.GoalMaxLength);

        Assert.Equal("備用", ContextNoteFileFormat.Parse(ContextNoteFileFormat.Compose("t", tooLong, Content), "備用").Goal);
        Assert.Equal(atLimit, ContextNoteFileFormat.Parse(ContextNoteFileFormat.Compose("t", atLimit, Content), "備用").Goal);
    }

    [Fact]
    public void Parse_OnlyContentWithoutHeader_UsesFallbackGoal()
    {
        Assert.Equal(("備用", Content), ContextNoteFileFormat.Parse(Content, "備用"));
    }

    [Fact]
    public void Parse_TitleLineChanged_DoesNotAffectGoalOrContent()
    {
        var text = ContextNoteFileFormat.Compose("舊標題", "目標", Content).Replace("# 舊標題", "# 改過的標題", StringComparison.Ordinal);

        Assert.Equal(("目標", Content), ContextNoteFileFormat.Parse(text, "備用"));
    }

    [Fact]
    public void Compose_NewlinesInGoalAndTitle_BecomeSpaces()
    {
        var text = ContextNoteFileFormat.Compose("標\n題", "第一行\r\n第二行\n第三行\r第四行", Content);

        Assert.StartsWith("# 標 題\n\n**目標**：第一行 第二行 第三行 第四行\n\n", text, StringComparison.Ordinal);
        Assert.Equal(("第一行 第二行 第三行 第四行", Content), ContextNoteFileFormat.Parse(text, "備用"));
    }

    [Fact]
    public void Parse_CrlfFile_ParsesTheSame()
    {
        var text = ContextNoteFileFormat.Compose("對話標題", "目標", Content).Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(("目標", Content), ContextNoteFileFormat.Parse(text, "備用"));
    }

    [Fact]
    public void Parse_NoRecognizedHeading_TitleAndGoalAreStrippedAndTheRestIsContent()
    {
        var text = "# 對話標題\n\n**目標**：目標文字\n\n- 純文字的清單";

        var (goal, content) = ContextNoteFileFormat.Parse(text, "備用");

        Assert.Equal("目標文字", goal);
        Assert.Equal(ContextNoteDocument.Parse("- 純文字的清單").ToMarkdown(), content);
    }

    [Fact]
    public void Parse_EmptyFile_GivesFallbackAndEmptyContent()
    {
        Assert.Equal(("備用", ContextNoteDocument.Empty.ToMarkdown()), ContextNoteFileFormat.Parse("", "備用"));
    }
}
