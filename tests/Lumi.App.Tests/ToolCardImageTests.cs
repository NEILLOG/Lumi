using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.App.Tests;

/// <summary>22F：工具卡片顯示生成的圖片——檔案存在與遺失的狀態，從歷史重建時也一樣。</summary>
public class ToolCardImageTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-card-images-" + Guid.NewGuid().ToString("N"));

    public ToolCardImageTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private ToolCardViewModel NewCard() => new("c1", "generate_image", "產生圖片", ToolPermission.Network, null, "{}",
        new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider());

    [Fact]
    public void Result_with_an_existing_image_shows_it_as_present()
    {
        var path = Path.Combine(_directory, "20261028-093015.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        var card = NewCard();

        card.Complete(new ToolResultBlock("c1", "已產生圖片", false, "產生圖片", null, "已產生圖片", 3, ToolApproval.AllowedOnce, null, [path]));

        Assert.True(card.HasImages);
        var image = Assert.Single(card.Images);
        Assert.Equal(path, image.FilePath);
        Assert.Equal("20261028-093015.png", image.FileName);
        Assert.True(image.Exists);
    }

    [Fact]
    public void Result_whose_image_was_moved_or_deleted_is_marked_missing()
    {
        var path = Path.Combine(_directory, "gone.png");
        var card = NewCard();

        card.Complete(new ToolResultBlock("c1", "已產生圖片", false, "產生圖片", null, "已產生圖片", 3, ToolApproval.AllowedOnce, null, [path]));

        var image = Assert.Single(card.Images);
        Assert.False(image.Exists);
        Assert.Equal("gone.png", image.FileName);
    }

    [Fact]
    public void Result_without_images_has_no_image_section()
    {
        var card = NewCard();

        card.Complete(new ToolResultBlock("c1", "好", false, "產生圖片", null, "好", 3, ToolApproval.AllowedOnce));

        Assert.False(card.HasImages);
        Assert.Empty(card.Images);
    }
}
