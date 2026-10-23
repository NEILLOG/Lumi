using Lumi.Core.Context;
using Xunit;

namespace Lumi.Tools.Tests.Files;

public sealed class ContextInspectorTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FileToolOptions _options = new();

    public void Dispose() => _tree.Dispose();

    private ContextInspector Inspector() => new(_options);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    // ---- 種類 ----

    [Fact]
    public async Task Classify_Folder() =>
        Assert.Equal(ContextKind.Folder, await Inspector().ClassifyAsync(_tree.Dir("d")));

    [Fact]
    public async Task Classify_TextFile() =>
        Assert.Equal(ContextKind.File, await Inspector().ClassifyAsync(_tree.Write("a.txt", "x")));

    [Fact]
    public async Task Classify_EmptyFile_IsAFile() =>
        Assert.Equal(ContextKind.File, await Inspector().ClassifyAsync(_tree.Write("empty.txt", "")));

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0 })]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 })]
    public async Task Classify_ImagesByContent_EvenWithAWrongExtension(byte[] data)
    {
        var path = _tree.Path_("notes.txt");   // 副檔名是騙人的
        File.WriteAllBytes(path, data);

        Assert.Equal(ContextKind.Image, await Inspector().ClassifyAsync(path));
    }

    [Fact]
    public async Task Classify_AnImageWithoutAnImageExtension_IsStillAnImage()
    {
        var path = _tree.Path_("screenshot");
        File.WriteAllBytes(path, Png);

        Assert.Equal(ContextKind.Image, await Inspector().ClassifyAsync(path));
    }

    [Fact]
    public async Task Classify_ATextFileNamedPng_IsAFile()
    {
        Assert.Equal(ContextKind.File, await Inspector().ClassifyAsync(_tree.Write("fake.png", "其實是文字")));
    }

    [Fact]
    public async Task Classify_MissingPath_IsNull() =>
        Assert.Null(await Inspector().ClassifyAsync(_tree.Path_("gone")));

    // ---- 檔案狀態 ----

    [Fact]
    public async Task File_SmallText_HasNoProblem_AndReportsItsSize()
    {
        var status = await Inspector().InspectAsync(ContextKind.File, _tree.Write("a.txt", "12345"));

        Assert.Equal(ContextItemProblem.None, status.Problem);
        Assert.Equal(5, status.SizeBytes);
        Assert.False(status.HasProblem);
    }

    [Fact]
    public async Task File_BiggerThanTheEmbedLimit_IsTooLarge_ButStillUsableByTools()
    {
        _options.EmbedMaxFileBytes = 10;

        var status = await Inspector().InspectAsync(ContextKind.File, _tree.Write("big.log", new string('x', 100)));

        Assert.Equal(ContextItemProblem.TooLarge, status.Problem);
        Assert.Equal(100, status.SizeBytes);
    }

    [Fact]
    public async Task File_Binary_IsNotText()
    {
        var path = _tree.Path_("a.bin");
        File.WriteAllBytes(path, [0, 1, 2, 0, 0]);

        Assert.Equal(ContextItemProblem.NotText, (await Inspector().InspectAsync(ContextKind.File, path)).Problem);
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("sheet.XLSX")]
    [InlineData("macro.xlsm")]
    [InlineData("deck.pptx")]
    [InlineData("paper.PDF")]
    public async Task File_OfficeOrPdf_IsDocumentByTool_NotNotText(string name)
    {
        var path = _tree.Write(name, "small");

        var status = await Inspector().InspectAsync(ContextKind.File, path);

        Assert.Equal(ContextItemProblem.DocumentByTool, status.Problem);
        Assert.True(status.HasProblem);
        Assert.Equal(5, status.SizeBytes);
    }

    [Theory]
    [InlineData("legacy.doc")]
    [InlineData("legacy.xls")]
    [InlineData("legacy.ppt")]
    [InlineData("data.docm")]
    public async Task File_LegacyOrUnlistedOfficeFormat_StaysNotText(string name)
    {
        // 舊格式 read_document 不支援，所以不能標成「用 read_document 讀取」
        var path = _tree.Path_(name);
        File.WriteAllBytes(path, [0xD0, 0xCF, 0x11, 0xE0, 0, 0]);

        Assert.Equal(ContextItemProblem.NotText, (await Inspector().InspectAsync(ContextKind.File, path)).Problem);
    }

    [Fact]
    public async Task File_OfficeNameThatIsMissing_IsMissing() =>
        Assert.Equal(ContextItemProblem.Missing, (await Inspector().InspectAsync(ContextKind.File, _tree.Path_("gone.docx"))).Problem);

    [Fact]
    public async Task File_Missing_IsMissing() =>
        Assert.Equal(ContextItemProblem.Missing, (await Inspector().InspectAsync(ContextKind.File, _tree.Path_("gone.txt"))).Problem);

    [Fact]
    public async Task File_ThatBecameAFolder_IsNotText() =>
        Assert.Equal(ContextItemProblem.NotText, (await Inspector().InspectAsync(ContextKind.File, _tree.Dir("d"))).Problem);

    [Fact]
    public async Task File_TheSameLimitsAsTheBuilder_SoTheChipMatchesWhatIsSent()
    {
        // chip 說「太大」的檔案，快照建構器就真的不嵌入；反過來也一樣
        _options.EmbedMaxFileBytes = 50;
        var small = _tree.Write("small.txt", new string('a', 50));
        var big = _tree.Write("big.txt", new string('a', 51));
        var builder = new ContextAttachmentBuilder(_options, new NullAttachmentStore(), new HeuristicTokenEstimator());
        ContextItem Item(string path) => new(ContextItem.NewId(), ContextKind.File, path, Path.GetFileName(path), DateTimeOffset.UtcNow, null);

        var built = await builder.BuildAsync([Item(small), Item(big)]);

        Assert.Equal(ContextItemProblem.None, (await Inspector().InspectAsync(ContextKind.File, small)).Problem);
        Assert.Equal(ContextItemProblem.TooLarge, (await Inspector().InspectAsync(ContextKind.File, big)).Problem);
        Assert.Equal([true, false], built.Blocks.Cast<Lumi.Core.Chat.ContextBlock>().Select(b => b.Embedded));
    }

    // ---- 資料夾 ----

    [Fact]
    public async Task Folder_CountsItsEntries_SkippingIgnoredNames()
    {
        _tree.Write("p/a.txt");
        _tree.Write("p/src/b.cs");
        _tree.Write("p/node_modules/x/y.js");
        _tree.Write("p/.git/config");

        var status = await Inspector().InspectAsync(ContextKind.Folder, _tree.Path_("p"));

        Assert.Equal(ContextItemProblem.None, status.Problem);
        Assert.Equal(3, status.EntryCount);   // a.txt、src/、src/b.cs
        Assert.False(status.EntryCountCapped);
    }

    [Fact]
    public async Task Folder_Empty_HasZeroEntries()
    {
        var status = await Inspector().InspectAsync(ContextKind.Folder, _tree.Dir("empty"));

        Assert.Equal(0, status.EntryCount);
    }

    [Fact]
    public void FolderTree_Count_StopsAtTheCap()
    {
        for (var i = 0; i < 50; i++)
            _tree.Write($"p/f{i:D2}.txt");

        var (count, capped) = FolderTree.Count(_tree.Path_("p"), 10);

        Assert.Equal(10, count);
        Assert.True(capped);
    }

    [Fact]
    public void FolderTree_Count_BelowTheCap_IsExact()
    {
        for (var i = 0; i < 5; i++)
            _tree.Write($"p/f{i}.txt");

        var (count, capped) = FolderTree.Count(_tree.Path_("p"), 10);

        Assert.Equal(5, count);
        Assert.False(capped);
    }

    [SkippableFact]
    public void FolderTree_Count_DoesNotFollowSymlinkLoops()
    {
        _tree.Dir("p/a");
        Skip.IfNot(_tree.TryLinkDirectory("p/a/loop", _tree.Path_("p")), "這個環境不能建立符號連結");

        var (count, capped) = FolderTree.Count(_tree.Path_("p"), 1_000);

        Assert.False(capped);
        Assert.Equal(2, count);   // a/ 與那個連結本身
    }

    [Fact]
    public async Task Folder_Missing_IsMissing() =>
        Assert.Equal(ContextItemProblem.Missing, (await Inspector().InspectAsync(ContextKind.Folder, _tree.Path_("gone"))).Problem);

    // ---- 圖片 ----

    [Fact]
    public async Task Image_Valid_HasNoProblem()
    {
        var path = _tree.Path_("a.png");
        File.WriteAllBytes(path, Png);

        var status = await Inspector().InspectAsync(ContextKind.Image, path);

        Assert.Equal(ContextItemProblem.None, status.Problem);
        Assert.Equal(Png.Length, status.SizeBytes);
    }

    [Fact]
    public async Task Image_WithAnUnsupportedFormat_IsFlagged()
    {
        var path = _tree.Path_("a.bmp");
        File.WriteAllBytes(path, [0x42, 0x4D, 0, 0, 0, 0, 0, 0]);

        Assert.Equal(ContextItemProblem.UnsupportedImage, (await Inspector().InspectAsync(ContextKind.Image, path)).Problem);
    }

    [Fact]
    public async Task Image_TooLarge_IsFlagged()
    {
        var path = _tree.Path_("big.png");
        File.WriteAllBytes(path, [.. Png, .. new byte[ImageFormats.MaxBytes]]);

        Assert.Equal(ContextItemProblem.ImageTooLarge, (await Inspector().InspectAsync(ContextKind.Image, path)).Problem);
    }

    [Fact]
    public async Task Image_Missing_IsMissing() =>
        Assert.Equal(ContextItemProblem.Missing, (await Inspector().InspectAsync(ContextKind.Image, _tree.Path_("gone.png"))).Problem);
}
