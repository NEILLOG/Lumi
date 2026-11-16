using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35C：書記回覆的解析（計畫 4.6「書記的回覆」）。</summary>
public class ScribeOutputParserTests
{
    [Theory]
    [InlineData("<no_change/>")]
    [InlineData("<no_change />")]
    [InlineData("<no_change></no_change>")]
    public void NoChangeForms_AreNoChange(string answer)
    {
        var output = ScribeOutputParser.Parse(answer);

        Assert.Equal(new ScribeOutput(NoChange: true, Note: null, GoalHint: null), output);
    }

    [Fact]
    public void Note_IsTrimmedContent()
    {
        var output = ScribeOutputParser.Parse("好的，整理如下：\n<note>\n## 問題拆解\n- 甲\n</note>");

        Assert.Equal(new ScribeOutput(NoChange: false, Note: "## 問題拆解\n- 甲", GoalHint: null), output);
    }

    [Fact]
    public void Note_WithGoalHint_HasBoth()
    {
        var output = ScribeOutputParser.Parse("<note>\n## 問題拆解\n</note>\n<goal_hint>  改看成本  </goal_hint>");

        Assert.Equal(new ScribeOutput(NoChange: false, Note: "## 問題拆解", GoalHint: "改看成本"), output);
    }

    [Fact]
    public void NoChange_WithGoalHint_HasBoth()
    {
        var output = ScribeOutputParser.Parse("<no_change/>\n<goal_hint>目標偏了</goal_hint>");

        Assert.Equal(new ScribeOutput(NoChange: true, Note: null, GoalHint: "目標偏了"), output);
    }

    [Fact]
    public void GoalHintLongerThanTheLimit_IsCut()
    {
        var hint = new string('字', ContextNoteLimits.GoalHintMaxLength + 50);

        var output = ScribeOutputParser.Parse($"<no_change/><goal_hint>{hint}</goal_hint>");

        Assert.NotNull(output);
        Assert.Equal(new string('字', ContextNoteLimits.GoalHintMaxLength), output.GoalHint);
    }

    [Fact]
    public void EmptyGoalHint_IsNull()
    {
        var output = ScribeOutputParser.Parse("<no_change/><goal_hint>  </goal_hint>");

        Assert.Null(output!.GoalHint);
    }

    [Fact]
    public void NeitherNoteNorNoChange_IsNull()
    {
        Assert.Null(ScribeOutputParser.Parse("我覺得可以這樣做。"));
    }

    [Fact]
    public void NoteWithoutClosingTag_IsNull()
    {
        Assert.Null(ScribeOutputParser.Parse("<note>\n## 問題拆解\n- 甲"));
    }

    [Fact]
    public void NoteContainingTheClosingTagText_TakesTheLastOne()
    {
        var output = ScribeOutputParser.Parse("<note>\n- 甲 提到 </note> 這幾個字\n- 乙\n</note>");

        Assert.Equal("- 甲 提到 </note> 這幾個字\n- 乙", output!.Note);
    }

    [Fact]
    public void NoChangeAndNote_NoteWins()
    {
        var output = ScribeOutputParser.Parse("<no_change/><note>內容</note>");

        Assert.Equal(new ScribeOutput(NoChange: false, Note: "內容", GoalHint: null), output);
    }
}
