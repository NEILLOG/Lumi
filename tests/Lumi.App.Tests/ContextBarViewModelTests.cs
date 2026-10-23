using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.App.Tests;

public class ContextBarViewModelTests
{
    private sealed class Rig
    {
        public InMemoryConversationContextStore Store { get; } = new();
        public InMemoryConversationStore Conversations { get; } = new();
        public FakeContextInspector Inspector { get; } = new();
        public FakeContextBuilder Builder { get; } = new();
        public FakeFilePicker Picker { get; } = new();
        public FakeClipboardImage Clipboard { get; } = new();
        public FakeAttachmentStore Attachments { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public ContextBarViewModel Bar { get; }
        public string ConversationId { get; private set; } = "";

        public Rig()
        {
            Bar = new ContextBarViewModel(Store, Conversations, Inspector, Builder, new HeuristicTokenEstimator(), Picker, Clipboard, Attachments, Time);
        }

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            rig.ConversationId = (await rig.Conversations.CreateAsync("c")).Id;
            await rig.Bar.LoadAsync(rig.ConversationId);
            return rig;
        }
    }

    // ---- 載入 ----

    [Fact]
    public async Task NoConversation_ShowsNothing_AndCannotChange()
    {
        var rig = new Rig();

        await rig.Bar.LoadAsync(null);

        Assert.False(rig.Bar.HasItems);
        Assert.False(rig.Bar.CanChange);
        Assert.False(rig.Bar.AddFileCommand.CanExecute(null));
        Assert.Equal(0, rig.Bar.EstimatedTokens);
    }

    [Fact]
    public async Task Load_ShowsTheConversationsItems_WithTheirStatus()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File, new ContextItemStatus(ContextItemProblem.None, 2048));
        await rig.Store.AddAsync(rig.ConversationId, ContextKind.File, "/a.txt", "a.txt");

        await rig.Bar.LoadAsync(rig.ConversationId);

        var chip = Assert.Single(rig.Bar.Items);
        Assert.Equal("a.txt", chip.DisplayName);
        Assert.Equal("2 KB", chip.Detail);
        Assert.False(chip.IsSent);
        Assert.True(rig.Bar.HasItems);
    }

    [Fact]
    public async Task SwitchingConversation_ShowsOnlyThatConversationsItems_AndClearsMessages()
    {
        var rig = await Rig.CreateAsync();
        var other = await rig.Conversations.CreateAsync("other");
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Store.AddAsync(rig.ConversationId, ContextKind.File, "/a.txt", "a.txt");
        await rig.Bar.LoadAsync(rig.ConversationId);
        rig.Bar.Error = "舊的錯誤";
        rig.Bar.Notice = "舊的說明";

        await rig.Bar.LoadAsync(other.Id);

        Assert.Empty(rig.Bar.Items);
        Assert.Null(rig.Bar.Error);
        Assert.Null(rig.Bar.Notice);
    }

    // ---- 加入 ----

    [Fact]
    public async Task AddPaths_ClassifiesEachPath_AndCreatesChips()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/proj", ContextKind.Folder, new ContextItemStatus(ContextItemProblem.None, null, 1234))
            .Know("/notes.md", ContextKind.File, new ContextItemStatus(ContextItemProblem.None, 100))
            .Know("/shot.png", ContextKind.Image, new ContextItemStatus(ContextItemProblem.None, 5000));

        var added = await rig.Bar.AddPathsAsync(["/proj", "/notes.md", "/shot.png"]);

        Assert.Equal(3, added);
        Assert.Equal([ContextKind.Folder, ContextKind.File, ContextKind.Image], rig.Bar.Items.Select(c => c.Kind));
        Assert.Equal(["proj", "notes.md", "shot.png"], rig.Bar.Items.Select(c => c.DisplayName));
        Assert.Equal("1,234 個項目", rig.Bar.Items[0].Detail);
        Assert.Equal(3, (await rig.Store.GetAsync(rig.ConversationId)).Count);
    }

    [Fact]
    public async Task AddPaths_SamePathTwice_DoesNotAddASecondChip()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File);

        await rig.Bar.AddPathsAsync(["/a.txt"]);
        await rig.Bar.AddPathsAsync(["/a.txt", "/a.txt"]);

        Assert.Single(rig.Bar.Items);
    }

    [Fact]
    public async Task AddPaths_ReportsMissingPaths_ButStillAddsTheOthers()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/ok.txt", ContextKind.File);

        var added = await rig.Bar.AddPathsAsync(["/ok.txt", "/gone.txt", "/also/gone.md"]);

        Assert.Equal(1, added);
        Assert.Equal(["ok.txt"], rig.Bar.Items.Select(c => c.DisplayName));
        Assert.True(rig.Bar.HasError);
        Assert.Equal("找不到：gone.txt、gone.md", rig.Bar.Error);
    }

    [Fact]
    public async Task AddPaths_AllMissing_AddsNothing_AndReportsIt()
    {
        var rig = await Rig.CreateAsync();

        var added = await rig.Bar.AddPathsAsync(["/gone.txt"]);

        Assert.Equal(0, added);
        Assert.False(rig.Bar.HasItems);
        Assert.True(rig.Bar.HasError);
    }

    [Fact]
    public async Task AddPaths_PathsWithTrailingSeparators_UseTheFolderName()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/Users/我/專案/", ContextKind.Folder);

        await rig.Bar.AddPathsAsync(["/Users/我/專案/"]);

        Assert.Equal("專案", Assert.Single(rig.Bar.Items).DisplayName);
    }

    [Fact]
    public async Task AddPaths_ClearsThePreviousErrorAndNotice()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        rig.Bar.Error = "舊";
        rig.Bar.Notice = "舊";

        await rig.Bar.AddPathsAsync(["/a.txt"]);

        Assert.Null(rig.Bar.Error);
        Assert.Null(rig.Bar.Notice);
    }

    [Fact]
    public async Task AddFileCommand_UsesThePicker_AndACancelledPickerDoesNothing()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File).Know("/b.txt", ContextKind.File);

        await rig.Bar.AddFileCommand.ExecuteAsync(null);   // 取消：沒有選任何檔案
        Assert.False(rig.Bar.HasItems);

        rig.Picker.Files.AddRange(["/a.txt", "/b.txt"]);
        await rig.Bar.AddFileCommand.ExecuteAsync(null);

        Assert.Equal(["a.txt", "b.txt"], rig.Bar.Items.Select(c => c.DisplayName));
        Assert.Equal(2, rig.Picker.FilePicks);
    }

    [Fact]
    public async Task AddFolderCommand_UsesThePicker()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/proj", ContextKind.Folder);

        await rig.Bar.AddFolderCommand.ExecuteAsync(null);   // 取消
        Assert.False(rig.Bar.HasItems);
        rig.Picker.Folder = "/proj";
        await rig.Bar.AddFolderCommand.ExecuteAsync(null);

        Assert.Equal(ContextKind.Folder, Assert.Single(rig.Bar.Items).Kind);
    }

    // ---- 貼上截圖 ----

    [Fact]
    public async Task PasteImage_SavesItAsAnAttachment_AndAddsAnImageChipWithATimeName()
    {
        var rig = await Rig.CreateAsync();
        rig.Clipboard.Image = new ClipboardImage([1, 2, 3], "image/png");
        rig.Inspector.Know("/attachments/" + new string('0', 63) + "1.png", ContextKind.Image);

        var handled = await rig.Bar.PasteImageAsync();

        Assert.True(handled);
        var chip = Assert.Single(rig.Bar.Items);
        Assert.Equal(ContextKind.Image, chip.Kind);
        Assert.Equal("貼上的截圖 12:00", chip.DisplayName);
        Assert.Single(rig.Attachments.Files);
    }

    [Fact]
    public async Task PasteImage_WithNoImageOnTheClipboard_IsNotHandled_SoTextPasteProceeds()
    {
        var rig = await Rig.CreateAsync();

        Assert.False(await rig.Bar.PasteImageAsync());
        Assert.False(rig.Bar.HasItems);
    }

    [Fact]
    public async Task PasteImage_WhenSavingFails_ShowsAnErrorInsteadOfThrowing()
    {
        var rig = await Rig.CreateAsync();
        rig.Clipboard.Image = new ClipboardImage([1], "image/bmp");
        rig.Attachments.Throw = true;

        var handled = await rig.Bar.PasteImageAsync();

        Assert.True(handled);
        Assert.True(rig.Bar.HasError);
        Assert.False(rig.Bar.HasItems);
    }

    // ---- 狀態與說明文字 ----

    [Theory]
    [InlineData(ContextItemProblem.Missing, "找不到")]
    [InlineData(ContextItemProblem.NotText, "不是文字檔")]
    [InlineData(ContextItemProblem.TooLarge, "太大，由模型按需讀取")]
    [InlineData(ContextItemProblem.DocumentByTool, "由模型用 read_document 讀取")]
    [InlineData(ContextItemProblem.UnsupportedImage, "不支援的圖片格式")]
    [InlineData(ContextItemProblem.ImageTooLarge, "圖片太大")]
    public async Task ProblemChips_ExplainWhy_AndAreStillAdded(ContextItemProblem problem, string expected)
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/x", ContextKind.File, new ContextItemStatus(problem));

        await rig.Bar.AddPathsAsync(["/x"]);

        var chip = Assert.Single(rig.Bar.Items);
        Assert.True(chip.HasProblem);
        Assert.Equal(expected, chip.Detail);
        Assert.Contains(expected, chip.Tooltip);
    }

    [Fact]
    public async Task FolderEntryCount_IsFormatted_AndMarkedWhenCapped()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/small", ContextKind.Folder, new ContextItemStatus(ContextItemProblem.None, null, 42))
            .Know("/huge", ContextKind.Folder, new ContextItemStatus(ContextItemProblem.None, null, 10_000, true));

        await rig.Bar.AddPathsAsync(["/small", "/huge"]);

        Assert.Equal(["42 個項目", "10,000+ 個項目"], rig.Bar.Items.Select(c => c.Detail));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void FormatSize(long bytes, string expected) => Assert.Equal(expected, ContextChipViewModel.FormatSize(bytes));

    [Fact]
    public async Task SentItems_AreMarked_PendingOnesAreNot()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File).Know("/b.txt", ContextKind.File);
        var a = await rig.Store.AddAsync(rig.ConversationId, ContextKind.File, "/a.txt", "a.txt");
        await rig.Store.AddAsync(rig.ConversationId, ContextKind.File, "/b.txt", "b.txt");
        await rig.Store.MarkEmbeddedAsync(rig.ConversationId, [a.Id], "m1");

        await rig.Bar.LoadAsync(rig.ConversationId);

        Assert.Equal([true, false], rig.Bar.Items.Select(c => c.IsSent));
        Assert.Equal("已帶入", rig.Bar.Items[0].SentLabel);
        Assert.Null(rig.Bar.Items[1].SentLabel);
        Assert.Equal(["b.txt"], rig.Bar.PendingChips().Select(c => c.DisplayName));
    }

    // ---- 移除 ----

    [Fact]
    public async Task Remove_APendingChip_RemovesItWithoutANotice()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Bar.AddPathsAsync(["/a.txt"]);

        await rig.Bar.RemoveCommand.ExecuteAsync(rig.Bar.Items[0]);

        Assert.False(rig.Bar.HasItems);
        Assert.Empty(await rig.Store.GetAsync(rig.ConversationId));
        Assert.Null(rig.Bar.Notice);
    }

    [Fact]
    public async Task Remove_ASentChip_ExplainsThatTheModelStillHasSeenIt()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        var item = await rig.Store.AddAsync(rig.ConversationId, ContextKind.File, "/a.txt", "a.txt");
        await rig.Store.MarkEmbeddedAsync(rig.ConversationId, [item.Id], "m1");
        await rig.Bar.LoadAsync(rig.ConversationId);

        await rig.Bar.RemoveCommand.ExecuteAsync(rig.Bar.Items[0]);

        Assert.False(rig.Bar.HasItems);
        Assert.True(rig.Bar.HasNotice);
        Assert.Equal(Strings.Context_RemovedSentNote, rig.Bar.Notice);
    }

    [Fact]
    public async Task Remove_Null_DoesNothing()
    {
        var rig = await Rig.CreateAsync();

        await rig.Bar.RemoveCommand.ExecuteAsync(null);

        Assert.False(rig.Bar.HasNotice);
    }

    // ---- 回應中 ----

    [Fact]
    public async Task WhileLocked_NothingCanBeAddedOrRemoved_AndUnlockingRestoresIt()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File).Know("/b.txt", ContextKind.File);
        await rig.Bar.AddPathsAsync(["/a.txt"]);

        rig.Bar.IsLocked = true;

        Assert.False(rig.Bar.CanChange);
        Assert.False(rig.Bar.AddFileCommand.CanExecute(null));
        Assert.False(rig.Bar.AddFolderCommand.CanExecute(null));
        Assert.False(rig.Bar.RemoveCommand.CanExecute(rig.Bar.Items[0]));
        Assert.Equal(0, await rig.Bar.AddPathsAsync(["/b.txt"]));   // 拖放也不行
        Assert.False(await rig.Bar.PasteImageAsync());
        Assert.Single(rig.Bar.Items);

        rig.Bar.IsLocked = false;

        Assert.True(rig.Bar.AddFileCommand.CanExecute(null));
        Assert.Equal(1, await rig.Bar.AddPathsAsync(["/b.txt"]));
    }

    // ---- token 預估 ----

    [Fact]
    public async Task Estimate_IsHistoryPlusPendingItemsPlusTheDraft()
    {
        var rig = await Rig.CreateAsync();
        await rig.Conversations.AppendMessageAsync(rig.ConversationId, new ChatMessage("u", ChatRole.User, [new TextBlock(new string('a', 400))], DateTimeOffset.UtcNow));
        await rig.Conversations.AppendMessageAsync(rig.ConversationId, new ChatMessage("a", ChatRole.Assistant, [new TextBlock(new string('b', 400))], DateTimeOffset.UtcNow));
        rig.Inspector.Know("/a.txt", ContextKind.File).Know("/b.txt", ContextKind.File);
        await rig.Bar.LoadAsync(rig.ConversationId);
        var historyOnly = rig.Bar.EstimatedTokens;
        Assert.InRange(historyOnly, 200, 220);   // 兩則各約 100 token，加每則的固定開銷

        await rig.Bar.AddPathsAsync(["/a.txt", "/b.txt"]);
        Assert.Equal(historyOnly + 200, rig.Bar.EstimatedTokens);   // 兩個待送出項目，每個 100

        rig.Bar.UpdateDraft(new string('c', 40));
        Assert.Equal(historyOnly + 200 + 10, rig.Bar.EstimatedTokens);

        rig.Bar.UpdateDraft("");
        Assert.Equal(historyOnly + 200, rig.Bar.EstimatedTokens);
    }

    [Fact]
    public async Task Estimate_SentItemsNoLongerCountAsPending()
    {
        var rig = await Rig.CreateAsync();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        var item = await rig.Store.AddAsync(rig.ConversationId, ContextKind.File, "/a.txt", "a.txt");
        await rig.Bar.LoadAsync(rig.ConversationId);
        var pending = rig.Bar.EstimatedTokens;

        await rig.Store.MarkEmbeddedAsync(rig.ConversationId, [item.Id], "m1");
        await rig.Bar.RefreshAsync();

        Assert.Equal(100, pending);
        Assert.Equal(0, rig.Bar.EstimatedTokens);   // 已經在歷史裡（歷史的估算由訊息本身提供）
    }

    [Fact]
    public async Task OverTheContextWindow_IsFlagged_AndNotWhenTheWindowIsUnknown()
    {
        var rig = await Rig.CreateAsync();
        rig.Builder.PendingTokensPerItem = 5_000;
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.Bar.AddPathsAsync(["/a.txt"]);

        rig.Bar.ContextWindow = 0;
        Assert.False(rig.Bar.IsOverLimit);

        rig.Bar.ContextWindow = 10_000;
        Assert.False(rig.Bar.IsOverLimit);

        rig.Bar.ContextWindow = 4_000;
        Assert.True(rig.Bar.IsOverLimit);
        Assert.Contains("4K", rig.Bar.OverLimitText);
    }

    [Fact]
    public async Task TokensText_UsesTheShortFormat()
    {
        var rig = await Rig.CreateAsync();
        rig.Builder.PendingTokensPerItem = 18_200;
        rig.Inspector.Know("/a.txt", ContextKind.File);

        await rig.Bar.AddPathsAsync(["/a.txt"]);

        Assert.Equal("≈ 18K tokens", rig.Bar.TokensText);
    }
}
