using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests;

public class ToolImageTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ToolImage Image(byte tag, int extra = 0) =>
        new([.. Png, tag, .. new byte[extra]], "image/png", $"截圖{tag}.png");

    /// <summary>記錄存檔內容的假附件儲存；可以指定哪幾次呼叫丟例外。</summary>
    private sealed class RecordingAttachmentStore : IAttachmentStore
    {
        public List<(byte[] Data, string MediaType)> Saved { get; } = [];

        public HashSet<int> FailOnCalls { get; } = [];

        private int _calls;

        public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
        {
            var call = ++_calls;
            if (FailOnCalls.Contains(call))
                throw new IOException("磁碟已滿");
            Saved.Add((data.ToArray(), mediaType));
            return Task.FromResult($"file{Saved.Count}.png");
        }

        public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static async Task<(ToolResultBlock Block, ConversationService Service)> Run(
        ToolResult result, IAttachmentStore? attachments)
    {
        var tool = new FakeTool("shot") { Behavior = (_, _) => Task.FromResult(result) };
        var store = new InMemoryConversationStore();
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "shot", """{"text":"x"}""")), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, new FakeModelCatalog(), new ToolRegistry([tool]), new ScriptedApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            attachments: attachments);
        var conversation = await store.CreateAsync("測試");

        var events = await Collect(service.SendAsync(conversation.Id, "截圖"));

        return (Assert.Single(events.OfType<ToolCallCompleted>()).Result, service);
    }

    [Fact]
    public async Task Two_images_are_saved_and_recorded_on_the_result_block()
    {
        var attachments = new RecordingAttachmentStore();

        var (block, _) = await Run(new ToolResult(false, "已截圖", "截圖") { Images = [Image(1), Image(2)] }, attachments);

        Assert.Equal(2, attachments.Saved.Count);
        Assert.Equal("image/png", attachments.Saved[0].MediaType);
        Assert.NotNull(block.Images);
        Assert.Equal(["file1.png", "file2.png"], block.Images!.Select(i => i.FileName));
        Assert.Equal(["截圖1.png", "截圖2.png"], block.Images!.Select(i => i.DisplayName));
        Assert.All(block.Images!, i => Assert.Equal("image/png", i.MediaType));
        Assert.Equal("已截圖", block.Content);
    }

    [Fact]
    public async Task More_than_the_limit_only_saves_the_first_two()
    {
        var attachments = new RecordingAttachmentStore();

        var (block, _) = await Run(new ToolResult(false, "已截圖", null) { Images = [Image(1), Image(2), Image(3)] }, attachments);

        Assert.Equal(BrowserLimits.MaxScreenshotImages, attachments.Saved.Count);
        Assert.Equal(2, block.Images!.Count);
        Assert.Equal(Image(2).Data, attachments.Saved[1].Data);
    }

    [Fact]
    public async Task An_oversized_image_is_skipped()
    {
        var attachments = new RecordingAttachmentStore();
        var huge = Image(9, extra: (int)BrowserLimits.MaxScreenshotBytes);   // 超過上限

        var (block, _) = await Run(new ToolResult(false, "已截圖", null) { Images = [huge, Image(2)] }, attachments);

        Assert.Single(attachments.Saved);
        Assert.Equal(Image(2).Data, attachments.Saved[0].Data);
        Assert.Equal("截圖2.png", Assert.Single(block.Images!).DisplayName);
    }

    [Fact]
    public async Task An_image_exactly_at_the_limit_is_kept()
    {
        var attachments = new RecordingAttachmentStore();
        var data = new byte[BrowserLimits.MaxScreenshotBytes];
        Png.CopyTo(data, 0);

        var (block, _) = await Run(new ToolResult(false, "已截圖", null) { Images = [new ToolImage(data, "image/png", "大圖.png")] }, attachments);

        Assert.Single(attachments.Saved);
        Assert.Single(block.Images!);
    }

    [Fact]
    public async Task A_failing_save_skips_that_image_and_keeps_the_text()
    {
        var attachments = new RecordingAttachmentStore();
        attachments.FailOnCalls.Add(1);

        var (block, _) = await Run(new ToolResult(false, "已截圖", "摘要") { Images = [Image(1), Image(2)] }, attachments);

        Assert.False(block.IsError);
        Assert.Equal("已截圖", block.Content);
        Assert.Equal("摘要", block.Summary);
        Assert.Equal("截圖2.png", Assert.Single(block.Images!).DisplayName);
    }

    [Fact]
    public async Task When_every_save_fails_images_is_null()
    {
        var attachments = new RecordingAttachmentStore();
        attachments.FailOnCalls.UnionWith([1, 2]);

        var (block, _) = await Run(new ToolResult(false, "已截圖", null) { Images = [Image(1), Image(2)] }, attachments);

        Assert.Null(block.Images);
        Assert.Equal("已截圖", block.Content);
    }

    [Fact]
    public async Task Without_an_attachment_store_the_images_are_dropped_without_throwing()
    {
        var (block, _) = await Run(new ToolResult(false, "已截圖", "摘要") { Images = [Image(1)] }, attachments: null);

        Assert.Null(block.Images);
        Assert.Equal("已截圖", block.Content);
        Assert.Equal("摘要", block.Summary);
    }

    [Fact]
    public async Task A_result_without_images_has_null_images()
    {
        var attachments = new RecordingAttachmentStore();

        var (block, _) = await Run(ToolResult.Ok("沒有圖"), attachments);

        Assert.Null(block.Images);
        Assert.Empty(attachments.Saved);
    }
}
