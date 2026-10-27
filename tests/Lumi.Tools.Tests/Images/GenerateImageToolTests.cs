using Lumi.Core.Chat;
using Lumi.Core.Images;
using Lumi.Core.Tools;
using Lumi.Tools.Images;
using Lumi.Tools.Tests.Files;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;
using static Lumi.Tools.Tests.Images.ImageTestSupport;

namespace Lumi.Tools.Tests.Images;

/// <summary>22F：generate_image——未設定的錯誤、尺寸驗證、存檔與重名、結果不帶 Images、生成失敗的訊息。</summary>
public sealed class GenerateImageToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeImageGenerator _generator = new();

    public void Dispose() => _tree.Dispose();

    private GenerateImageTool Tool(Memory.FakeModelCatalog catalog) =>
        new(catalog, _generator, new GeneratedImageStore(Path.Combine(_tree.Root, "圖片", "Lumi")));

    private Task<ToolResult> Run(Memory.FakeModelCatalog catalog, object args, TimeProvider? time = null) =>
        Tool(catalog).ExecuteAsync(Json(args), ImageContext(ScopeOf(_tree), time), default);

    [Fact]
    public void Descriptor_is_network_workbench_only_240_seconds_in_the_images_group()
    {
        var d = Tool(ConfiguredCatalog()).Descriptor;

        Assert.Equal("generate_image", d.Name);
        Assert.Equal("產生圖片", d.DisplayName);
        Assert.Equal(ToolPermission.Network, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(240), d.Timeout);
        Assert.Equal(ToolGroups.Images, d.Group);
        Assert.False(d.RequiresScope);
    }

    [Fact]
    public async Task NotConfigured_IsAnError_AndNothingIsSent()
    {
        var result = await Run(new Memory.FakeModelCatalog(), new { prompt = "一隻貓" });

        Assert.True(result.IsError);
        Assert.Contains("設定 › AI 連線 › 圖片生成", result.Content);
        Assert.Empty(_generator.Generations);
    }

    [Fact]
    public async Task DeletedConnection_IsNotConfigured()
    {
        var catalog = ConfiguredCatalog();
        catalog.Current = catalog.Current with { Connections = [] };

        var result = await Run(catalog, new { prompt = "一隻貓" });

        Assert.True(result.IsError);
        Assert.Empty(_generator.Generations);
    }

    [Theory]
    [InlineData("512x512")]
    [InlineData("auto")]
    public async Task UnknownSize_IsAnError(string size)
    {
        var result = await Run(ConfiguredCatalog(), new { prompt = "一隻貓", size });

        Assert.True(result.IsError);
        Assert.Empty(_generator.Generations);
    }

    [Fact]
    public async Task Missing_size_uses_the_settings_default()
    {
        await Run(ConfiguredCatalog("1536x1024"), new { prompt = "  一隻貓  " });

        var request = Assert.Single(_generator.Generations);
        Assert.Equal("1536x1024", request.Size);
        Assert.Equal("一隻貓", request.Prompt);
        Assert.Equal("gpt-image-2", request.ModelId);
        Assert.Equal("gw", request.Connection.Id);
    }

    [Fact]
    public async Task Success_saves_the_png_under_the_pictures_folder_and_returns_the_path_only()
    {
        var result = await Run(ConfiguredCatalog(), new { prompt = "一隻貓", size = "1024x1536" });

        Assert.False(result.IsError);
        var expected = Path.Combine(_tree.Root, "圖片", "Lumi", "20261028-093015.png");
        Assert.Equal(expected, Assert.Single(result.ImageFiles));
        Assert.Equal(FakeImageGenerator.Png, File.ReadAllBytes(expected));
        Assert.Contains(expected, result.Content);
        Assert.Contains("1024x1536", result.Content);
        Assert.Empty(result.Images);
        Assert.Equal("已產生圖片", result.Summary);
    }

    [Fact]
    public async Task Same_second_gets_a_numbered_name()
    {
        var catalog = ConfiguredCatalog();

        var first = await Run(catalog, new { prompt = "甲" });
        var second = await Run(catalog, new { prompt = "乙" });

        Assert.EndsWith("20261028-093015.png", Assert.Single(first.ImageFiles));
        Assert.EndsWith("20261028-093015-2.png", Assert.Single(second.ImageFiles));
        Assert.True(File.Exists(Assert.Single(second.ImageFiles)));
    }

    [Fact]
    public async Task JpegResult_is_saved_with_a_jpg_extension()
    {
        _generator.Result = new(FakeImageGenerator.Jpeg, "image/jpeg", null);

        var result = await Run(ConfiguredCatalog(), new { prompt = "一隻貓" });

        Assert.EndsWith(".jpg", Assert.Single(result.ImageFiles));
    }

    [Fact]
    public async Task RevisedPrompt_is_reported_to_the_model()
    {
        _generator.Result = new(FakeImageGenerator.Png, "image/png", "A cat in a hat");

        var result = await Run(ConfiguredCatalog(), new { prompt = "一隻貓" });

        Assert.Contains("A cat in a hat", result.Content);
    }

    [Fact]
    public async Task GenerationFailure_ReturnsTheMessageAsAnError()
    {
        _generator.Failure = new ImageGenerationException(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。");

        var result = await Run(ConfiguredCatalog(), new { prompt = "一隻貓" });

        Assert.True(result.IsError);
        Assert.Equal("已達到速率限制，請稍後再試。", result.Content);
        Assert.Empty(result.ImageFiles);
        Assert.False(Directory.Exists(Path.Combine(_tree.Root, "圖片", "Lumi")));
    }

    [Fact]
    public void Preview_shows_the_prompt_truncated_and_the_size()
    {
        var catalog = ConfiguredCatalog("1024x1024");
        var longPrompt = new string('字', 150);

        var preview = Tool(catalog).Preview(Json(new { prompt = longPrompt }), ImageContext(ScopeOf(_tree)));

        Assert.NotNull(preview.Detail);
        Assert.Contains(new string('字', 120) + "…", preview.Detail);
        Assert.DoesNotContain(new string('字', 121), preview.Detail);
        Assert.Contains("1024x1024", preview.Detail);
    }
}
