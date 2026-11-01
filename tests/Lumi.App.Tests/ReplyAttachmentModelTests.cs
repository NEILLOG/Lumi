using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Tarot;

namespace Lumi.App.Tests;

/// <summary>M25 回覆附件的資料（Phase 0）：工具卡片產生的附件、圖片縮圖尺寸、訊息上的附件清單。</summary>
public class ReplyAttachmentModelTests
{
    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private static ToolCardViewModel Card(string toolName, string argumentsJson = "{}") => new("c1", toolName, toolName, ToolPermission.ReadOnly, null, argumentsJson,
        new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());

    private const string ThreeCardJson = """
        {"spread_id":"three_card_timeline","spread_name":"三張牌","allow_reversed":true,"positions":[
          {"index":1,"position_name":"過去","layout":{"x":-1,"y":0,"rotation":0,"z":0},
           "card":{"id":"major_00","name_zh":"愚者","name_en":"The Fool","element":null,"orientation":"upright"}}]}
        """;

    [Theory]
    [InlineData(1024, 1024, 320, 320)]
    [InlineData(1536, 1024, 320, 213)]
    [InlineData(1024, 1536, 213, 320)]
    [InlineData(0, 0, 320, 320)]
    public void Image_thumbnail_fits_inside_320_square(int width, int height, double expectedWidth, double expectedHeight)
    {
        var image = ImageAttachmentViewModel.ForFile("c1", "/tmp/a.png", true, width, height);

        Assert.Equal(expectedWidth, image.DisplayWidth);
        Assert.Equal(expectedHeight, image.DisplayHeight);
    }

    [Fact]
    public void Image_states_are_exclusive()
    {
        var shown = ImageAttachmentViewModel.ForFile("c1", "/tmp/20261101-231205.png", exists: true);
        var missing = ImageAttachmentViewModel.ForFile("c1", "/tmp/gone.png", exists: false);
        var pending = ImageAttachmentViewModel.Pending("c1", 1536, 1024);

        Assert.True(shown.ShowsImage);
        Assert.False(shown.IsMissing);
        Assert.Equal("20261101-231205.png", shown.FileName);
        Assert.True(missing.IsMissing);
        Assert.False(missing.ShowsImage);
        Assert.True(pending.IsPending);
        Assert.False(pending.ShowsImage);
        Assert.False(pending.IsMissing);
        Assert.Equal("", pending.FileName);
    }

    [Fact]
    public void Pending_image_elapsed_text_follows_seconds()
    {
        var pending = ImageAttachmentViewModel.Pending("c1", 1024, 1024);
        var changed = new List<string?>();
        pending.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pending.ElapsedSeconds = 23;

        Assert.StartsWith("已 23 秒", pending.ElapsedText);
        Assert.Contains(nameof(ImageAttachmentViewModel.ElapsedText), changed);
    }

    [Fact]
    public void Tarot_result_becomes_one_tarot_attachment()
    {
        var card = Card(TarotToolNames.Draw);

        card.Complete(new ToolResultBlock("c1", ThreeCardJson, false, "塔羅抽牌", null, "三張牌", 3, ToolApproval.NotRequired));

        var attachment = Assert.IsType<TarotAttachmentViewModel>(Assert.Single(card.ReplyAttachments));
        Assert.Equal("c1", attachment.CallId);
        Assert.Equal("three_card_timeline", attachment.Spread.SpreadId);
    }

    [Fact]
    public void Failed_tarot_result_has_no_attachment()
    {
        var card = Card(TarotToolNames.Draw);

        card.Complete(new ToolResultBlock("c1", ThreeCardJson, true, "塔羅抽牌", null, "失敗", 3, ToolApproval.NotRequired));

        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public void Image_result_becomes_image_attachments_with_the_size_argument()
    {
        var card = Card("generate_image", """{"prompt":"貓","size":"1536x1024"}""");

        card.Complete(new ToolResultBlock("c1", "已產生圖片", false, "產生圖片", null, "已產生圖片", 3, ToolApproval.AllowedOnce, null, ["/nowhere/a.png"]));

        var image = Assert.IsType<ImageAttachmentViewModel>(Assert.Single(card.ReplyAttachments));
        Assert.Equal("/nowhere/a.png", image.FilePath);
        Assert.False(image.Exists);
        Assert.Equal(1536, image.PixelWidth);
        Assert.Equal(1024, image.PixelHeight);
        Assert.True(card.IsImageTool);
    }

    [Theory]
    [InlineData("{}", 1024, 1024)]
    [InlineData("""{"size":"1024x1536"}""", 1024, 1536)]
    [InlineData("""{"size":"大張"}""", 1024, 1024)]
    [InlineData("""{"size":"0x10"}""", 1024, 1024)]
    [InlineData("not json", 1024, 1024)]
    [InlineData("[1]", 1024, 1024)]
    public void Size_argument_falls_back_to_square(string json, int width, int height)
    {
        Assert.Equal((width, height), Card("generate_image", json).ImageSizeArgument());
    }

    [Fact]
    public void Ordinary_tool_has_no_attachments()
    {
        var card = Card("read_file");

        card.Complete(new ToolResultBlock("c1", "內容", false, "讀取檔案", null, "10 行", 3, ToolApproval.NotRequired));

        Assert.Empty(card.ReplyAttachments);
        Assert.False(card.IsImageTool);
    }

    [Fact]
    public void Message_reports_when_it_has_reply_attachments()
    {
        var message = MessageViewModel.StreamingAssistant();
        var changed = new List<string?>();
        message.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        message.ReplyAttachments.Add(new TarotAttachmentViewModel("c1", new TarotSpreadModel("單張牌", true, [])));

        Assert.True(message.HasReplyAttachments);
        Assert.Contains(nameof(MessageViewModel.HasReplyAttachments), changed);
        Assert.False(message.IsAttachmentHost);
        Assert.True(MessageViewModel.AttachmentHost().IsAttachmentHost);
    }
}
