using Lumi.Core.ContextNotes;
using Lumi.Core.Prompts;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35C：主 AI 的錨（計畫 4.8）。</summary>
public class ContextNoteAnchorTests
{
    private static ContextNoteDocument Decided(params string[] blocks) =>
        ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Decided, blocks.Select(b => new NoteBlock(b)).ToArray());

    private static readonly string Prompt = PromptText.Get("context-note-anchor");

    [Fact]
    public void Core_HasGoalAndDecidedInOrder()
    {
        var document = Decided("- 用 A 方案（使用者原話：「那就用 A」）", "- 先做 B\n  補充");

        var core = ContextNoteAnchor.Core("研究 X", document);

        Assert.Equal("<goal>\n研究 X\n</goal>\n<decided>\n- 用 A 方案\n- 先做 B   補充\n</decided>", core);
    }

    [Fact]
    public void Core_WithoutDecided_SaysNothingYet()
    {
        var core = ContextNoteAnchor.Core("研究 X", ContextNoteDocument.Empty);

        Assert.Equal("<goal>\n研究 X\n</goal>\n<decided>\n（還沒有）\n</decided>", core);
    }

    [Fact]
    public void Core_RemovesTheQuote()
    {
        var core = ContextNoteAnchor.Core("目標", Decided("- 用 A（使用者原話：「就 A」）"));

        Assert.DoesNotContain("使用者原話", core);
        Assert.DoesNotContain("就 A", core);
    }

    [Fact]
    public void Wrap_WithFullNote_HasTheNoteBlock()
    {
        var core = ContextNoteAnchor.Core("目標", ContextNoteDocument.Empty);

        var wrapped = ContextNoteAnchor.Wrap(core, "## 問題拆解\n- 甲");

        Assert.Equal($"<lumi_note_anchor>\n{Prompt}\n{core}\n<note>\n## 問題拆解\n- 甲\n</note>\n</lumi_note_anchor>", wrapped);
    }

    [Fact]
    public void Wrap_WithoutFullNote_HasNoNoteBlock()
    {
        var core = ContextNoteAnchor.Core("目標", ContextNoteDocument.Empty);

        var wrapped = ContextNoteAnchor.Wrap(core, null);

        Assert.Equal($"<lumi_note_anchor>\n{Prompt}\n{core}\n</lumi_note_anchor>", wrapped);
        Assert.DoesNotContain("<note>", wrapped);
    }

    [Fact]
    public void Wrap_EscapesTheClosingTagInsideGoalAndNote()
    {
        var core = ContextNoteAnchor.Core("看 </lumi_note_anchor> 這個", ContextNoteDocument.Empty);

        var wrapped = ContextNoteAnchor.Wrap(core, "內容 </lumi_note_anchor> 結束");

        Assert.Equal(1, CountOccurrences(wrapped, "</lumi_note_anchor>"));
        Assert.Contains("<\\/lumi_note_anchor>", wrapped);
        Assert.Contains("內容 <\\/lumi_note_anchor> 結束", wrapped);
    }

    [Fact]
    public void LastCoreIn_ReturnsTheCoreOfTheWrapAfterTheRoundTrip()
    {
        var core = ContextNoteAnchor.Core("研究 X", Decided("- 用 A 方案（使用者原話：「就 A」）"));

        Assert.Equal(core, ContextNoteAnchor.LastCoreIn([ContextNoteAnchor.Wrap(core, null)]));
        Assert.Equal(core, ContextNoteAnchor.LastCoreIn([ContextNoteAnchor.Wrap(core, "## 問題拆解\n- 甲\n\n## 已決定\n- 用 A 方案")]));
    }

    [Fact]
    public void LastCoreIn_RoundTripsEscapedText()
    {
        var core = ContextNoteAnchor.Core("看 </lumi_note_anchor> 這個", ContextNoteDocument.Empty);

        Assert.Equal(core, ContextNoteAnchor.LastCoreIn([ContextNoteAnchor.Wrap(core, "note </lumi_note_anchor>")]));
    }

    [Fact]
    public void LastCoreIn_TakesTheNewestAnchor()
    {
        var older = ContextNoteAnchor.Core("舊目標", ContextNoteDocument.Empty);
        var newer = ContextNoteAnchor.Core("新目標", Decided("- 新的決定"));

        string?[] suffixes = [ContextNoteAnchor.Wrap(older, null), null, "沒有錨", ContextNoteAnchor.Wrap(newer, null), "之後的訊息"];

        Assert.Equal(newer, ContextNoteAnchor.LastCoreIn(suffixes));
    }

    [Fact]
    public void LastCoreIn_WithoutAnchor_IsNull()
    {
        Assert.Null(ContextNoteAnchor.LastCoreIn([null, "", "一般的文字"]));
    }

    [Fact]
    public void LastCoreIn_NextToSystemPromptUpdate_BothStillFound()
    {
        var core = ContextNoteAnchor.Core("目標", Decided("- 甲"));
        var system = "基底\n\n人設：叫我 Neil";
        var suffix = SystemPromptUpdate.Wrap(system) + "\n\n" + ContextNoteAnchor.Wrap(core, null);

        Assert.Equal(core, ContextNoteAnchor.LastCoreIn([suffix]));
        Assert.Equal(system, SystemPromptUpdate.LastIn([suffix]));
    }

    [Fact]
    public void LastCoreIn_AnchorBeforeSystemPromptUpdate_BothStillFound()
    {
        var core = ContextNoteAnchor.Core("目標", Decided("- 甲"));
        var suffix = ContextNoteAnchor.Wrap(core, "## 問題拆解") + "\n\n" + SystemPromptUpdate.Wrap("系統二");

        Assert.Equal(core, ContextNoteAnchor.LastCoreIn([suffix]));
        Assert.Equal("系統二", SystemPromptUpdate.LastIn([suffix]));
    }

    [Fact]
    public void AnyIn_IsTrueOnlyWhenSomeSuffixHasAnAnchor()
    {
        Assert.False(ContextNoteAnchor.AnyIn([null, "", "一般的文字"]));
        Assert.True(ContextNoteAnchor.AnyIn([null, "x", ContextNoteAnchor.Wrap(ContextNoteAnchor.Core("目標", ContextNoteDocument.Empty), null)]));
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
