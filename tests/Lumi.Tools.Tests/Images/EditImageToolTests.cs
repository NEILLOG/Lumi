using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tools.Images;
using Lumi.Tools.Tests.Files;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;
using static Lumi.Tools.Tests.Images.ImageTestSupport;

namespace Lumi.Tools.Tests.Images;

/// <summary>22F：edit_image——三種來源（附件名稱、先前產生的路徑、範圍內的檔案）、其他路徑拒絕、格式與數量限制。</summary>
public sealed class EditImageToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FakeImageGenerator _generator = new();
    private readonly MessageConversations _conversations = new();
    private readonly MemoryAttachments _attachments = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private string PicturesFolder => Path.Combine(_tree.Root, "圖片", "Lumi");

    private EditImageTool Tool(Memory.FakeModelCatalog catalog) =>
        new(catalog, _generator, new GeneratedImageStore(PicturesFolder), _conversations, _attachments);

    private Task<ToolResult> Run(object args, Memory.FakeModelCatalog? catalog = null) =>
        Tool(catalog ?? ConfiguredCatalog()).ExecuteAsync(Json(args), ImageContext(ScopeOf(_tree)), default);

    private void AttachImage(string displayName, byte[] data)
    {
        const string name = "att-1.png";
        _attachments.Files[name] = data;
        _conversations.Messages.Add(UserWith(new ImageBlock(name, "image/png", displayName)));
    }

    [Fact]
    public void Descriptor_is_network_workbench_only_in_the_images_group()
    {
        var d = Tool(ConfiguredCatalog()).Descriptor;

        Assert.Equal("edit_image", d.Name);
        Assert.Equal("編輯圖片", d.DisplayName);
        Assert.Equal(ToolPermission.Network, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(ToolGroups.Images, d.Group);
    }

    [Fact]
    public async Task AttachedImage_is_found_by_its_display_name()
    {
        AttachImage("照片.png", FakeImageGenerator.Png);

        var result = await Run(new { prompt = "換背景", images = new[] { "照片.png" } });

        Assert.False(result.IsError);
        var edit = Assert.Single(_generator.Edits);
        var source = Assert.Single(edit.Images);
        Assert.Equal(FakeImageGenerator.Png, source.Data);
        Assert.Equal("image/png", source.MediaType);
        Assert.Equal("照片.png", source.FileName);
        Assert.Equal("換背景", edit.Prompt);
        Assert.Equal("1024x1024", edit.Size);
    }

    [Fact]
    public async Task Result_is_saved_as_an_edit_file_and_only_returns_the_path()
    {
        AttachImage("照片.png", FakeImageGenerator.Png);

        var result = await Run(new { prompt = "換背景", images = new[] { "照片.png" } });

        var expected = Path.Combine(PicturesFolder, "20261028-093015-edit.png");
        Assert.Equal(expected, Assert.Single(result.ImageFiles));
        Assert.True(File.Exists(expected));
        Assert.Empty(result.Images);
        Assert.Equal("已編輯圖片", result.Summary);
    }

    [Fact]
    public async Task Image_generated_earlier_in_this_conversation_is_found_by_path()
    {
        var produced = Path.Combine(PicturesFolder, "20261028-090000.png");
        Directory.CreateDirectory(PicturesFolder);
        File.WriteAllBytes(produced, FakeImageGenerator.Png);
        _conversations.Messages.Add(ToolWith(ProducedImage(produced)));

        var result = await Run(new { prompt = "改成夜景", images = new[] { produced } });

        Assert.False(result.IsError);
        Assert.Equal(FakeImageGenerator.Png, Assert.Single(_generator.Edits).Images[0].Data);
    }

    [Fact]
    public async Task File_in_the_pictures_folder_that_this_conversation_did_not_produce_is_a_plain_scope_file()
    {
        // 圖片資料夾在範圍內時，它只是範圍裡的一個檔案（第三種來源）；不是「先前產生的圖」也不需要對話紀錄
        Directory.CreateDirectory(PicturesFolder);
        var stray = Path.Combine(PicturesFolder, "stray.png");
        File.WriteAllBytes(stray, FakeImageGenerator.Png);

        var result = await Run(new { prompt = "x", images = new[] { stray } });

        Assert.False(result.IsError);
        Assert.Single(_generator.Edits);
    }

    [Fact]
    public async Task File_inside_the_scope_is_a_valid_source()
    {
        Directory.CreateDirectory(Path.Combine(_tree.Root, "pics"));
        File.WriteAllBytes(_tree.Path_("pics/a.jpg"), FakeImageGenerator.Jpeg);

        var result = await Run(new { prompt = "x", images = new[] { "pics/a.jpg" } });

        Assert.False(result.IsError);
        var source = Assert.Single(_generator.Edits).Images[0];
        Assert.Equal("image/jpeg", source.MediaType);
        Assert.Equal("a.jpg", source.FileName);
    }

    [Fact]
    public async Task Path_outside_the_scope_is_rejected_and_nothing_is_sent()
    {
        var outsideFile = _outside.Path_("secret.png");
        File.WriteAllBytes(outsideFile, FakeImageGenerator.Png);

        var absolute = await Run(new { prompt = "x", images = new[] { outsideFile } });
        var relative = await Run(new { prompt = "x", images = new[] { "../secret.png" } });

        Assert.True(absolute.IsError);
        Assert.True(relative.IsError);
        Assert.Empty(_generator.Edits);
    }

    [Fact]
    public async Task Gif_source_is_rejected()
    {
        AttachImage("動圖.png", FakeImageGenerator.Gif);

        var result = await Run(new { prompt = "x", images = new[] { "動圖.png" } });

        Assert.True(result.IsError);
        Assert.Empty(_generator.Edits);
    }

    [Fact]
    public async Task Lost_attachment_is_an_error()
    {
        _conversations.Messages.Add(UserWith(new ImageBlock("gone.png", "image/png", "遺失.png")));

        var result = await Run(new { prompt = "x", images = new[] { "遺失.png" } });

        Assert.True(result.IsError);
        Assert.Empty(_generator.Edits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Source_count_must_be_one_to_four(int count)
    {
        AttachImage("照片.png", FakeImageGenerator.Png);
        var images = Enumerable.Repeat("照片.png", count).ToArray();

        var result = await Run(new { prompt = "x", images });

        Assert.True(result.IsError);
        Assert.Empty(_generator.Edits);
    }

    [Fact]
    public async Task Four_sources_are_accepted()
    {
        AttachImage("照片.png", FakeImageGenerator.Png);

        var result = await Run(new { prompt = "合成", images = Enumerable.Repeat("照片.png", 4).ToArray() });

        Assert.False(result.IsError);
        Assert.Equal(4, Assert.Single(_generator.Edits).Images.Count);
    }

    [Fact]
    public async Task Not_configured_is_an_error_before_reading_any_source()
    {
        AttachImage("照片.png", FakeImageGenerator.Png);

        var result = await Run(new { prompt = "x", images = new[] { "照片.png" } }, new Memory.FakeModelCatalog());

        Assert.True(result.IsError);
        Assert.Empty(_generator.Edits);
    }

    [Fact]
    public async Task Generator_failure_returns_its_message()
    {
        AttachImage("照片.png", FakeImageGenerator.Png);
        _generator.Failure = new Lumi.Core.Images.ImageGenerationException(ChatFailureKind.BadRequest, "請求內容不被伺服器接受。");

        var result = await Run(new { prompt = "x", images = new[] { "照片.png" } });

        Assert.True(result.IsError);
        Assert.Equal("請求內容不被伺服器接受。", result.Content);
        Assert.Empty(result.ImageFiles);
    }
}
