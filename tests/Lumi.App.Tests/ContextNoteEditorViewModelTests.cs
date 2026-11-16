using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests;

/// <summary>M35 35H：研究本編輯模式的存檔、放棄、字數上限與貼圖插入位置。</summary>
public sealed class ContextNoteEditorViewModelTests
{
    private const string NoteId = "note-1";

    private static readonly ClipboardImage PngImage = new([1, 2, 3], "image/png");

    private sealed class Rig
    {
        public FakeContextNoteService Notes { get; } = new();
        public FakeClipboardImage Clipboard { get; } = new();
        public FakeConfirmService Confirm { get; } = new();
        public ContextNoteEditorViewModel Vm { get; }

        public Rig()
        {
            Vm = new ContextNoteEditorViewModel(Notes, Clipboard, Confirm);
        }
    }

    private static Rig BeginWith(string content)
    {
        var rig = new Rig();
        rig.Vm.Begin(NoteId, content);
        return rig;
    }

    // ---- 開始與存檔 ----

    [Fact]
    public void Begin_CallsBeginUserEdit_AndLoadsTheText()
    {
        var rig = BeginWith("## 已決定\n\n- 甲");

        Assert.Equal("## 已決定\n\n- 甲", rig.Vm.Text);
        Assert.False(rig.Vm.IsDirty);
        Assert.Contains($"BeginUserEdit:{NoteId}", rig.Notes.Calls);
    }

    [Fact]
    public async Task CommitAsync_WithoutChanges_PassesNull_AndFinishes()
    {
        var rig = BeginWith("原文");
        var finished = 0;
        rig.Vm.Finished += (_, _) => finished++;

        var ok = await rig.Vm.CommitAsync();

        Assert.True(ok);
        Assert.Contains($"EndUserEdit:{NoteId}:null", rig.Notes.Calls);
        Assert.Null(rig.Notes.LastEditContent);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task CommitAsync_WithChanges_PassesNewContent_AndFinishes()
    {
        var rig = BeginWith("原文");
        var finished = 0;
        rig.Vm.Finished += (_, _) => finished++;
        rig.Vm.Text = "改過的內容";

        Assert.True(rig.Vm.IsDirty);
        var ok = await rig.Vm.CommitAsync();

        Assert.True(ok);
        Assert.Contains($"EndUserEdit:{NoteId}:content", rig.Notes.Calls);
        Assert.Equal("改過的內容", rig.Notes.LastEditContent);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task CommitAsync_OverLimit_ReturnsFalse_DoesNotFinish_AndDoesNotCallService()
    {
        var rig = BeginWith("原文");
        var finished = 0;
        rig.Vm.Finished += (_, _) => finished++;
        rig.Vm.Text = new string('字', ContextNoteLimits.NoteMaxChars + 1);
        var callsBefore = rig.Notes.Calls.Count;

        Assert.True(rig.Vm.IsTooLong);
        Assert.False(rig.Vm.CommitCommand.CanExecute(null));
        var ok = await rig.Vm.CommitAsync();

        Assert.False(ok);
        Assert.Equal(0, finished);
        Assert.Equal(callsBefore, rig.Notes.Calls.Count);
        Assert.Equal(string.Format(Strings.ContextNote_Edit_TooLongFormat, ContextNoteLimits.NoteMaxChars, ContextNoteLimits.NoteMaxChars + 1),
            rig.Vm.TooLongText);
    }

    [Fact]
    public void CommitCommand_IsEnabled_WhenWithinLimit()
    {
        var rig = BeginWith("原文");

        Assert.True(rig.Vm.CommitCommand.CanExecute(null));
    }

    // ---- 放棄 ----

    [Fact]
    public async Task DiscardAsync_WithoutChanges_DoesNotAsk_AndEndsWithNull()
    {
        var rig = BeginWith("原文");
        var finished = 0;
        rig.Vm.Finished += (_, _) => finished++;

        var ok = await rig.Vm.DiscardAsync();

        Assert.True(ok);
        Assert.Empty(rig.Confirm.Asked);
        Assert.Contains($"EndUserEdit:{NoteId}:null", rig.Notes.Calls);
        Assert.Equal(1, finished);
    }

    [Fact]
    public async Task DiscardAsync_WithChanges_AsksFirst_AndCancelReturnsFalse()
    {
        var rig = BeginWith("原文");
        rig.Vm.Text = "改過";
        rig.Confirm.Answer = false;
        var finished = 0;
        rig.Vm.Finished += (_, _) => finished++;

        var ok = await rig.Vm.DiscardAsync();

        Assert.False(ok);
        Assert.Single(rig.Confirm.Asked);
        Assert.Equal(Strings.ContextNote_Edit_DiscardConfirm, rig.Confirm.Asked[0].Message);
        Assert.Equal(Strings.ContextNote_Edit_Discard, rig.Confirm.Asked[0].ConfirmText);
        Assert.DoesNotContain(rig.Notes.Calls, call => call.StartsWith("EndUserEdit:", StringComparison.Ordinal));
        Assert.Equal(0, finished);
    }

    [Fact]
    public async Task DiscardAsync_WithChanges_ConfirmedEndsWithNull_AndFinishes()
    {
        var rig = BeginWith("原文");
        rig.Vm.Text = "改過";
        rig.Confirm.Answer = true;
        var finished = 0;
        rig.Vm.Finished += (_, _) => finished++;

        var ok = await rig.Vm.DiscardAsync();

        Assert.True(ok);
        Assert.Contains($"EndUserEdit:{NoteId}:null", rig.Notes.Calls);
        Assert.Null(rig.Notes.LastEditContent);
        Assert.Equal(1, finished);
    }

    // ---- 貼圖 ----

    [Fact]
    public async Task PasteImage_AtStartOfFirstLine_InsertsAfterThatLine()
    {
        var rig = BeginWith("abc\ndef");
        rig.Clipboard.Image = PngImage;
        rig.Notes.NextImagePath = "images/x.png";

        var caret = await rig.Vm.PasteImageAsync(0);

        const string line = "![圖片](images/x.png)\n";
        Assert.Equal("abc\n" + line + "def", rig.Vm.Text);
        Assert.Equal("abc\n".Length + line.Length, caret);
        Assert.Null(rig.Vm.ErrorText);
    }

    [Fact]
    public async Task PasteImage_InTheMiddleOfALine_InsertsAfterThatLine()
    {
        var rig = BeginWith("first\nsecond\nthird");
        rig.Clipboard.Image = PngImage;
        rig.Notes.NextImagePath = "images/x.png";

        // 游標在 "first" 中間（第 2 個字之後）
        var caret = await rig.Vm.PasteImageAsync(2);

        const string line = "![圖片](images/x.png)\n";
        Assert.Equal("first\n" + line + "second\nthird", rig.Vm.Text);
        Assert.Equal("first\n".Length + line.Length, caret);
    }

    [Fact]
    public async Task PasteImage_OnLastLineWithoutTrailingNewline_AddsNewlinesAroundTheImage()
    {
        var rig = BeginWith("abc\ndef");
        rig.Clipboard.Image = PngImage;
        rig.Notes.NextImagePath = "images/x.png";

        var caret = await rig.Vm.PasteImageAsync(6);

        const string text = "abc\ndef\n![圖片](images/x.png)\n";
        Assert.Equal(text, rig.Vm.Text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public async Task PasteImage_WhenStorageRejectsTheImage_ShowsError_AndChangesNothing()
    {
        var rig = BeginWith("原文");
        rig.Clipboard.Image = PngImage;
        rig.Notes.ThrowOnAddImage = new ArgumentException("不支援");

        var caret = await rig.Vm.PasteImageAsync(0);

        Assert.Equal(-1, caret);
        Assert.Equal(Strings.ContextNote_Edit_PasteImageFailed, rig.Vm.ErrorText);
        Assert.Equal("原文", rig.Vm.Text);
    }

    [Fact]
    public async Task PasteImage_WithoutImageOnClipboard_DoesNothing()
    {
        var rig = BeginWith("原文");
        rig.Clipboard.Image = null;

        var caret = await rig.Vm.PasteImageAsync(2);

        Assert.Equal(-1, caret);
        Assert.Equal("原文", rig.Vm.Text);
        Assert.Null(rig.Vm.ErrorText);
        Assert.DoesNotContain(rig.Notes.Calls, call => call.StartsWith("AddImage:", StringComparison.Ordinal));
    }
}
