using Lumi.Core.ContextNotes;

namespace Lumi.Core.Tests.ContextNotes;

/// <summary>M35 35B：<see cref="ContextNoteValidator.Check"/>（計畫 4.6 的程式端檢查）。</summary>
public class ContextNoteValidatorTests
{
    private static ContextNoteDocument Breakdown(params string[] texts) =>
        ContextNoteDocument.Empty.WithSection(ContextNoteDocument.Breakdown, texts.Select(t => new NoteBlock(t)).ToArray());

    private static string Md(params string[] texts) => Breakdown(texts).ToMarkdown();

    private static string[] Numbered(int count, string prefix = "- 項目") => Enumerable.Range(1, count).Select(i => $"{prefix}{i}").ToArray();

    [Fact]
    public void Check_ValidOutput_IsNone()
    {
        var before = Breakdown("- a", "- b");

        Assert.Equal(NoteRejectReason.None, ContextNoteValidator.Check(before, before.ToMarkdown(), guardChanges: true));
    }

    [Fact]
    public void Check_WrongStructure_IsWrongStructure()
    {
        Assert.Equal(NoteRejectReason.WrongStructure, ContextNoteValidator.Check(Breakdown(), "只是一段文字", guardChanges: false));
    }

    [Fact]
    public void Check_TooLong_IsTooLong()
    {
        var scribe = Md(new string('字', ContextNoteLimits.NoteMaxChars));

        Assert.True(scribe.Length > ContextNoteLimits.NoteMaxChars);
        Assert.Equal(NoteRejectReason.TooLong, ContextNoteValidator.Check(Breakdown(), scribe, guardChanges: false));
    }

    [Fact]
    public void Check_HalfOfEightBlocksChangedFive_IsTooManyChanges()
    {
        var before = Breakdown(Numbered(8));
        var changed = Md([.. Numbered(3), .. Numbered(5, "- 改")]);

        Assert.Equal(NoteRejectReason.TooManyChanges, ContextNoteValidator.Check(before, changed, guardChanges: true));
    }

    [Fact]
    public void Check_ExactlyHalfChangedFour_IsNone()
    {
        var before = Breakdown(Numbered(8));
        var changed = Md([.. Numbered(4), .. Numbered(4, "- 改")]);

        Assert.Equal(NoteRejectReason.None, ContextNoteValidator.Check(before, changed, guardChanges: true));
    }

    [Fact]
    public void Check_FewerThanEightBlocks_IsNotChecked()
    {
        var before = Breakdown(Numbered(7));
        var changed = Md(Numbered(7, "- 全部換掉"));

        Assert.Equal(NoteRejectReason.None, ContextNoteValidator.Check(before, changed, guardChanges: true));
    }

    [Fact]
    public void Check_GuardChangesFalse_IsNotChecked()
    {
        var before = Breakdown(Numbered(8));
        var changed = Md(Numbered(8, "- 全部換掉"));

        Assert.Equal(NoteRejectReason.None, ContextNoteValidator.Check(before, changed, guardChanges: false));
    }

    [Fact]
    public void Check_PlaceholdersAreNotCountedInTheDenominator()
    {
        // 一般區塊 7 個（不到 8 個就不檢查）加 2 個佔位符；就算一般區塊全改，也不擋。
        var before = Breakdown([.. Numbered(7), "- [[保護:1]]", "[[保護:2]]"]);
        var changed = Md([.. Numbered(7, "- 全部換掉"), "- [[保護:1]]", "[[保護:2]]"]);

        Assert.Equal(NoteRejectReason.None, ContextNoteValidator.Check(before, changed, guardChanges: true));
    }

    [Fact]
    public void Check_EightNormalBlocksPlusTwoPlaceholders_FourChanged_IsNone()
    {
        var before = Breakdown([.. Numbered(8), "- [[保護:1]]", "[[保護:2]]"]);
        var changed = Md([.. Numbered(4), .. Numbered(4, "- 改"), "- [[保護:1]]", "[[保護:2]]"]);

        Assert.Equal(NoteRejectReason.None, ContextNoteValidator.Check(before, changed, guardChanges: true));
    }

    [Fact]
    public void Check_EightNormalBlocksPlusTwoPlaceholders_FiveChanged_IsTooManyChanges()
    {
        var before = Breakdown([.. Numbered(8), "- [[保護:1]]", "[[保護:2]]"]);
        var changed = Md([.. Numbered(3), .. Numbered(5, "- 改"), "- [[保護:1]]", "[[保護:2]]"]);

        Assert.Equal(NoteRejectReason.TooManyChanges, ContextNoteValidator.Check(before, changed, guardChanges: true));
    }

    [Fact]
    public void Check_StructureIsCheckedBeforeLength()
    {
        // 結構錯又太長：先回結構錯。
        var scribe = "只是文字" + new string('字', ContextNoteLimits.NoteMaxChars);

        Assert.Equal(NoteRejectReason.WrongStructure, ContextNoteValidator.Check(Breakdown(), scribe, guardChanges: false));
    }
}
