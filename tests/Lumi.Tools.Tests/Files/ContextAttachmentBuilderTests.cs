using System.Runtime.Versioning;
using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Xunit;

namespace Lumi.Tools.Tests.Files;

public sealed class ContextAttachmentBuilderTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FileToolOptions _options = new();
    private readonly MemoryAttachmentStore _attachments = new();

    public void Dispose() => _tree.Dispose();

    private sealed class MemoryAttachmentStore : IAttachmentStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
        {
            var name = $"file{Files.Count + 1}.{ImageFormats.Extension(mediaType)}";
            Files[name] = data.ToArray();
            return Task.FromResult(name);
        }

        public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) =>
            Task.FromResult(Files.GetValueOrDefault(fileName));

        public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
    }

    private ContextAttachmentBuilder Builder() => new(_options, _attachments, new HeuristicTokenEstimator());

    private static ContextItem Item(ContextKind kind, string path, string? name = null) =>
        new(ContextItem.NewId(), kind, path, name ?? Path.GetFileName(path.TrimEnd('/', '\\')), DateTimeOffset.UtcNow, null);

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    // ---- 檔案 ----

    [Fact]
    public async Task SmallTextFile_IsEmbedded_WithItsContent()
    {
        var file = _tree.Write("a.txt", "第一行\n第二行 ❤️‍🔥\n");
        var item = Item(ContextKind.File, file);

        var result = await Builder().BuildAsync([item]);

        var block = Assert.IsType<ContextBlock>(Assert.Single(result.Blocks));
        Assert.True(block.Embedded);
        Assert.Equal("第一行\n第二行 ❤️‍🔥\n", block.Content);
        Assert.Equal(("a.txt", file), (block.DisplayName, block.Path));
        Assert.Equal([item.Id], result.ItemIds);
    }

    [Fact]
    public async Task EmptyFile_IsEmbeddedAsEmptyContent()
    {
        var file = _tree.Write("empty.txt", "");

        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.File, file)])).Blocks));

        Assert.True(block.Embedded);
        Assert.Equal("", block.Content);
    }

    [Fact]
    public async Task Utf8BomAndUtf16_AreDecoded()
    {
        var bom = _tree.Path_("bom.txt");
        File.WriteAllText(bom, "內容", new UTF8Encoding(true));
        var u16 = _tree.Path_("u16.txt");
        File.WriteAllText(u16, "你好", new UnicodeEncoding(false, true));

        var result = await Builder().BuildAsync([Item(ContextKind.File, bom), Item(ContextKind.File, u16)]);

        Assert.Equal(["內容", "你好"], result.Blocks.Cast<ContextBlock>().Select(b => b.Content));
    }

    [Fact]
    public async Task BigFile_IsNotEmbedded_ButTheModelIsToldToUseReadFile()
    {
        _options.EmbedMaxFileBytes = 100;
        var file = _tree.Write("big.log", new string('x', 500));

        var result = await Builder().BuildAsync([Item(ContextKind.File, file)]);

        var block = Assert.IsType<ContextBlock>(Assert.Single(result.Blocks));
        Assert.False(block.Embedded);
        Assert.Null(block.Content);
        Assert.Contains("太大", block.Note);
        Assert.Contains("read_file", block.Note);
        Assert.Single(result.ItemIds);   // 沒放內容也算處理過，不會重複送
    }

    [Fact]
    public async Task TotalBudget_StopsEmbeddingLaterFiles()
    {
        _options.EmbedMaxFileBytes = 100;
        _options.EmbedMaxTotalBytes = 150;
        var files = Enumerable.Range(1, 3).Select(i => _tree.Write($"f{i}.txt", new string('a', 80))).ToList();

        var result = await Builder().BuildAsync(files.Select(f => Item(ContextKind.File, f)).ToList());

        Assert.Equal([true, false, false], result.Blocks.Cast<ContextBlock>().Select(b => b.Embedded));
        Assert.Contains("上限", ((ContextBlock)result.Blocks[1]).Note);
    }

    [Fact]
    public async Task BinaryFile_IsNotEmbedded_WithAnExplanation()
    {
        var file = _tree.Path_("a.bin");
        File.WriteAllBytes(file, [0, 1, 2, 3, 0, 0]);

        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.File, file)])).Blocks));

        Assert.False(block.Embedded);
        Assert.Contains("不是文字檔", block.Note);
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("sheet.xlsx")]
    [InlineData("macro.xlsm")]
    [InlineData("deck.pptx")]
    [InlineData("paper.pdf")]
    public async Task OfficeOrPdf_IsNotEmbedded_EvenWhenSmall_AndPointsToReadDocument(string name)
    {
        var file = _tree.Write(name, "很小的內容");

        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.File, file)])).Blocks));

        Assert.False(block.Embedded);
        Assert.Null(block.Content);
        Assert.Equal("由模型用 read_document 讀取", block.Note);
    }

    [Fact]
    public async Task InvalidUtf8_IsNotEmbedded_InsteadOfGarbled()
    {
        var file = _tree.Path_("latin1.txt");
        File.WriteAllBytes(file, Encoding.Latin1.GetBytes("café ÿþ bad"));

        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.File, file)])).Blocks));

        Assert.False(block.Embedded);
        Assert.Contains("UTF-8", block.Note);
    }

    [Fact]
    public async Task MissingFile_ProducesAnExplanationBlock_NotAnException()
    {
        var item = Item(ContextKind.File, _tree.Path_("gone.txt"));

        var result = await Builder().BuildAsync([item]);

        var block = Assert.IsType<ContextBlock>(Assert.Single(result.Blocks));
        Assert.False(block.Embedded);
        Assert.Contains("找不到", block.Note);
        Assert.Equal([item.Id], result.ItemIds);
    }

    [Fact]
    public async Task AFolderGivenAsAFile_IsExplained()
    {
        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.File, _tree.Dir("d"))])).Blocks));

        Assert.False(block.Embedded);
        Assert.Contains("資料夾", block.Note);
    }

    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task UnreadableFile_IsExplained_NotAnException()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows 沒有 chmod");
        var file = _tree.Write("locked.txt", "秘密");
        File.SetUnixFileMode(file, UnixFileMode.None);
        var canStillRead = true;
        try
        {
            using var probe = File.OpenRead(file);
        }
        catch (UnauthorizedAccessException)
        {
            canStillRead = false;
        }
        Skip.If(canStillRead, "以有權限的身分執行，無法模擬讀不到");

        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.File, file)])).Blocks));

        Assert.False(block.Embedded);
        Assert.Contains("沒有權限", block.Note);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // ---- 資料夾 ----

    [Fact]
    public async Task Folder_ProducesATreeWithFoldersFirst_AndAnEntryCount()
    {
        _tree.Write("proj/src/a.cs");
        _tree.Write("proj/src/deep/b.cs");
        _tree.Write("proj/readme.md");

        var result = await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Path_("proj"))]);

        var block = Assert.IsType<ContextBlock>(Assert.Single(result.Blocks));
        Assert.True(block.Embedded);
        Assert.Equal("proj/\n  src/\n    deep/\n      b.cs\n    a.cs\n  readme.md\n", block.Content);
        Assert.Equal(5, block.ItemCount);
    }

    [Fact]
    public async Task Folder_SkipsIgnoredNames_AndMarksSecretFiles()
    {
        _tree.Write("proj/node_modules/x/i.js");
        _tree.Write("proj/.git/config");
        _tree.Write("proj/bin/a.dll");
        _tree.Write("proj/.env", "K=1");
        _tree.Write("proj/.env.example", "K=");
        _tree.Write("proj/.ssh/id_rsa", "k");
        _tree.Write("proj/src/a.cs");

        var block = (ContextBlock)(await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Path_("proj"))])).Blocks[0];

        Assert.DoesNotContain("node_modules", block.Content);
        Assert.DoesNotContain(".git", block.Content);
        Assert.DoesNotContain("a.dll", block.Content);
        Assert.Contains(".env（機密，不會讀取）", block.Content);
        Assert.Contains(".env.example\n", block.Content);
        Assert.Contains(".ssh/（機密，不會讀取）", block.Content);
        Assert.DoesNotContain("id_rsa", block.Content);   // 不展開機密資料夾
    }

    [Fact]
    public async Task HugeFolder_IsCappedAtTheEntryLimit_AndSaysHowManyAreLeft()
    {
        _options.TreeMaxEntries = 10;
        for (var i = 0; i < 100; i++)
            _tree.Write($"proj/f{i:D3}.txt");

        var block = (ContextBlock)(await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Path_("proj"))])).Blocks[0];

        Assert.Equal(10, block.ItemCount);
        Assert.Equal(10, block.Content!.Split('\n').Count(l => l.StartsWith("  f")));
        Assert.Contains("還有 90 個項目沒有列出", block.Content);
    }

    [Fact]
    public async Task Folder_DepthIsLimited()
    {
        _options.TreeMaxDepth = 2;
        _tree.Write("proj/a/b/c/d.txt");

        var block = (ContextBlock)(await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Path_("proj"))])).Blocks[0];

        Assert.Contains("a/", block.Content);
        Assert.Contains("b/", block.Content);
        Assert.DoesNotContain("c/", block.Content);
    }

    [Fact]
    public async Task EmptyFolder_IsAnEmbeddedTreeWithJustTheName()
    {
        var block = (ContextBlock)(await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Dir("empty"))])).Blocks[0];

        Assert.True(block.Embedded);
        Assert.Equal("empty/\n", block.Content);
        Assert.Equal(0, block.ItemCount);
    }

    [Fact]
    public async Task MissingFolder_IsExplained()
    {
        var block = (ContextBlock)(await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Path_("gone"))])).Blocks[0];

        Assert.False(block.Embedded);
        Assert.Contains("找不到", block.Note);
    }

    [SkippableFact]
    public async Task Folder_DoesNotFollowSymlinks()
    {
        var outside = new TempTree();
        try
        {
            outside.Write("secret/s.txt");
            _tree.Dir("proj");
            Skip.IfNot(_tree.TryLinkDirectory("proj/escape", outside.Root), "這個環境不能建立符號連結");

            var block = (ContextBlock)(await Builder().BuildAsync([Item(ContextKind.Folder, _tree.Path_("proj"))])).Blocks[0];

            Assert.Contains("escape/（符號連結）", block.Content);
            Assert.DoesNotContain("s.txt", block.Content);
        }
        finally
        {
            outside.Dispose();
        }
    }

    // ---- 圖片 ----

    [Fact]
    public async Task Image_IsSavedToTheAttachmentStore_AndReferencedByName()
    {
        var file = _tree.Path_("截圖.png");
        File.WriteAllBytes(file, PngBytes);

        var result = await Builder().BuildAsync([Item(ContextKind.Image, file)]);

        var block = Assert.IsType<ImageBlock>(Assert.Single(result.Blocks));
        Assert.Equal("image/png", block.MediaType);
        Assert.Equal("截圖.png", block.DisplayName);
        Assert.Equal(PngBytes, _attachments.Files[block.FileName]);
    }

    [Fact]
    public async Task Image_FormatIsDetectedByContent_NotByExtension()
    {
        var jpegNamedPng = _tree.Path_("really-jpeg.png");
        File.WriteAllBytes(jpegNamedPng, [0xFF, 0xD8, 0xFF, 0xE0, 0, 0]);
        var textNamedPng = _tree.Path_("fake.png");
        File.WriteAllText(textNamedPng, "我其實是文字");

        var result = await Builder().BuildAsync([Item(ContextKind.Image, jpegNamedPng), Item(ContextKind.Image, textNamedPng)]);

        Assert.Equal("image/jpeg", Assert.IsType<ImageBlock>(result.Blocks[0]).MediaType);
        var rejected = Assert.IsType<ContextBlock>(result.Blocks[1]);
        Assert.False(rejected.Embedded);
        Assert.Contains("不支援的圖片格式", rejected.Note);
        Assert.Single(_attachments.Files);
    }

    [Fact]
    public async Task Image_TooLarge_IsExplained_AndNotStored()
    {
        var file = _tree.Path_("big.png");
        File.WriteAllBytes(file, [.. PngBytes, .. new byte[ImageFormats.MaxBytes]]);

        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.Image, file)])).Blocks));

        Assert.Contains("圖片太大", block.Note);
        Assert.Empty(_attachments.Files);
    }

    [Fact]
    public async Task MissingImage_IsExplained()
    {
        var block = Assert.IsType<ContextBlock>(Assert.Single((await Builder().BuildAsync([Item(ContextKind.Image, _tree.Path_("gone.png"))])).Blocks));

        Assert.Contains("找不到", block.Note);
    }

    // ---- 多個項目與估算 ----

    [Fact]
    public async Task MixedItems_KeepTheirOrder_AndEveryItemIsConsumed_EvenWhenSomeFail()
    {
        var items = new[]
        {
            Item(ContextKind.File, _tree.Write("a.txt", "甲")),
            Item(ContextKind.File, _tree.Path_("missing.txt")),
            Item(ContextKind.Folder, _tree.Dir("d")),
            Item(ContextKind.Image, WritePng("p.png")),
        };

        var result = await Builder().BuildAsync(items);

        Assert.Equal(items.Select(i => i.Id), result.ItemIds);
        Assert.Equal([typeof(ContextBlock), typeof(ContextBlock), typeof(ContextBlock), typeof(ImageBlock)], result.Blocks.Select(b => b.GetType()));
    }

    private string WritePng(string name)
    {
        var path = _tree.Path_(name);
        File.WriteAllBytes(path, PngBytes);
        return path;
    }

    [Fact]
    public async Task NoItems_ProduceNothing()
    {
        var result = await Builder().BuildAsync([]);

        Assert.Empty(result.Blocks);
        Assert.Empty(result.ItemIds);
    }

    [Fact]
    public async Task EstimateTokens_CountsFileTextAndImages_WithoutStoringAnything()
    {
        var file = _tree.Write("a.txt", new string('a', 4_000));
        var image = WritePng("p.png");

        var tokens = await Builder().EstimateTokensAsync([Item(ContextKind.File, file), Item(ContextKind.Image, image)]);

        Assert.InRange(tokens, 1_000 + 1_500, 1_000 + 1_500 + 600);   // 檔案文字約 1,000（加行號與標籤的開銷）＋圖片 1,500
        Assert.Empty(_attachments.Files);
    }

    [Fact]
    public async Task EstimateTokens_ForNoItems_IsZero() =>
        Assert.Equal(0, await Builder().EstimateTokensAsync([]));

    [Fact]
    public async Task Cancellation_StopsTheBuild()
    {
        var file = _tree.Write("a.txt", "x");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Builder().BuildAsync([Item(ContextKind.File, file)], cts.Token));
    }

    [Fact]
    public async Task UnicodeFileAndFolderNames_Work()
    {
        var file = _tree.Write("資料夾/筆記 📝.md", "好");

        var result = await Builder().BuildAsync([Item(ContextKind.File, file), Item(ContextKind.Folder, _tree.Path_("資料夾"))]);

        Assert.Equal("好", ((ContextBlock)result.Blocks[0]).Content);
        Assert.Contains("筆記 📝.md", ((ContextBlock)result.Blocks[1]).Content);
    }
}
