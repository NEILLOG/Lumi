using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;

namespace Lumi.App.Tests;

/// <summary>
/// M25 25E（R5）：產生圖片的佔位。等待允許與拒絕時沒有佔位；允許之後才出現、從允許那一刻計時；
/// 結束（成功、失敗、拒絕、停止）換成最終結果或清空；產生類工具允許之後不強制展開。
/// </summary>
public class ImageAttachmentPlaceholderTests
{
    private sealed class Responder : IToolApprovalResponder
    {
        public List<(string CallId, ApprovalDecision Decision)> Calls { get; } = [];

        public void Resolve(string callId, ApprovalDecision decision) => Calls.Add((callId, decision));
    }

    private const string ImageArguments = """{"prompt":"貓","size":"1536x1024"}""";

    private static ToolCardViewModel Card(FakeTimeProvider time, string toolName = "generate_image", string arguments = ImageArguments) =>
        new("c1", toolName, "產生圖片", ToolPermission.Network, null, arguments, new Responder(), new FakeClipboardService(), time);

    private static ToolApprovalRequest Request() =>
        new("c1", "generate_image", "產生圖片", ToolPermission.Network, new ToolPreview(null, "將產生圖片"), ImageArguments);

    private static List<ImageAttachmentViewModel> Images(ToolCardViewModel card) => card.ReplyAttachments.OfType<ImageAttachmentViewModel>().ToList();

    private static ToolResultBlock Ok(string path) =>
        new("c1", "已產生圖片", false, "產生圖片", null, "已產生圖片", 3, ToolApproval.AllowedOnce, null, [path]);

    [Fact]
    public void Placeholder_is_absent_while_waiting_for_approval()
    {
        var card = Card(new FakeTimeProvider());

        card.SetAwaiting(Request());

        Assert.Empty(card.ReplyAttachments);
        Assert.True(card.IsAwaiting);
    }

    [Fact]
    public void Placeholder_is_absent_after_denial()
    {
        var card = Card(new FakeTimeProvider());
        card.SetAwaiting(Request());

        card.ApplyResolved(ApprovalDecision.Deny);

        Assert.Equal(ToolCardState.Denied, card.State);
        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public void Placeholder_appears_after_allowance_with_the_requested_size()
    {
        var card = Card(new FakeTimeProvider());
        card.SetAwaiting(Request());

        card.ApplyResolved(ApprovalDecision.AllowOnce);

        var pending = Assert.Single(Images(card));
        Assert.True(pending.IsPending);
        Assert.Equal(1536, pending.PixelWidth);
        Assert.Equal(1024, pending.PixelHeight);
        Assert.Equal(320, pending.DisplayWidth);
        Assert.Equal(213, pending.DisplayHeight);
    }

    [Fact]
    public void Placeholder_appears_through_the_decide_command_path()
    {
        var card = Card(new FakeTimeProvider());
        card.SetAwaiting(Request());

        card.AllowOnceCommand.Execute(null);

        Assert.True(Assert.Single(Images(card)).IsPending);
    }

    [Fact]
    public void Placeholder_is_present_when_no_approval_is_needed()
    {
        // 沒有收到 ApprovalRequired 就是自動允許（排程授權、本次對話都允許）：執行中直接有佔位
        var card = Card(new FakeTimeProvider());

        Assert.True(Assert.Single(Images(card)).IsPending);
        Assert.False(card.IsForcedOpen);
    }

    [Fact]
    public void Completion_replaces_the_placeholder_with_the_file()
    {
        var card = Card(new FakeTimeProvider());
        var path = Path.Combine(Path.GetTempPath(), "20261102-101500.png");

        card.Complete(Ok(path));

        var image = Assert.Single(card.ReplyAttachments.OfType<ImageAttachmentViewModel>());
        Assert.False(image.IsPending);
        Assert.Equal(path, image.FilePath);
        Assert.Equal(1536, image.PixelWidth);
    }

    [Fact]
    public void Failure_clears_the_placeholder()
    {
        var card = Card(new FakeTimeProvider());

        card.Complete(new ToolResultBlock("c1", "失敗", true, "產生圖片", null, "失敗", 3, ToolApproval.AllowedOnce));

        Assert.Equal(ToolCardState.Failed, card.State);
        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public void Stop_without_result_clears_the_placeholder()
    {
        var card = Card(new FakeTimeProvider());

        card.MarkNoResult();

        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public void Elapsed_seconds_count_from_the_allowance_not_from_the_request()
    {
        var time = new FakeTimeProvider();
        var card = Card(time);
        card.SetAwaiting(Request());
        time.Advance(TimeSpan.FromSeconds(5));   // 等待允許的 5 秒不算

        card.ApplyResolved(ApprovalDecision.AllowOnce);
        time.Advance(TimeSpan.FromSeconds(23));
        var pending = Assert.Single(Images(card));
        pending.Tick(time.GetUtcNow());

        Assert.Equal(23, pending.ElapsedSeconds);
        Assert.StartsWith("已 23 秒", pending.ElapsedText);
    }

    [Fact]
    public void Elapsed_seconds_advance_for_an_automatically_allowed_run()
    {
        var time = new FakeTimeProvider();
        var pending = Assert.Single(Images(Card(time)));

        time.Advance(TimeSpan.FromSeconds(23));
        pending.Tick(time.GetUtcNow());

        Assert.Equal(23, pending.ElapsedSeconds);
    }

    [Fact]
    public void Finished_card_holds_no_placeholder_any_more()
    {
        var time = new FakeTimeProvider();
        var card = Card(time);
        Assert.Single(Images(card));
        card.Complete(Ok(Path.Combine(Path.GetTempPath(), "gone.png")));

        time.Advance(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(Images(card), image => image.IsPending);
    }

    [Fact]
    public void Image_card_waiting_for_approval_is_forced_open()
    {
        var card = Card(new FakeTimeProvider());

        card.SetAwaiting(Request());

        Assert.True(card.IsForcedOpen);
        Assert.True(card.IsOpen);
        Assert.False(card.CanToggle);
    }

    [Fact]
    public void Image_card_collapses_once_allowed_and_running()
    {
        var card = Card(new FakeTimeProvider());
        card.SetAwaiting(Request());

        card.ApplyResolved(ApprovalDecision.AllowOnce);

        Assert.Equal(ToolCardState.Running, card.State);
        Assert.False(card.IsForcedOpen);
        Assert.False(card.IsOpen);
        Assert.True(card.CanToggle);
    }

    [Fact]
    public void Image_card_collapses_when_no_approval_was_needed()
    {
        var card = Card(new FakeTimeProvider());

        Assert.False(card.IsForcedOpen);
        Assert.True(card.CanToggle);
    }

    [Fact]
    public void Ordinary_tool_stays_forced_open_while_running()
    {
        var card = Card(new FakeTimeProvider(), toolName: "read_file", arguments: "{}");

        Assert.Equal(ToolCardState.Running, card.State);
        Assert.True(card.IsForcedOpen);
        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public void Image_card_title_ends_with_running_text_and_has_no_seconds()
    {
        var time = new FakeTimeProvider();
        var card = Card(time);
        time.Advance(TimeSpan.FromSeconds(23));

        Assert.EndsWith(Strings.Tool_Running, card.Title);
        Assert.DoesNotContain("秒", card.Title);
    }
}
