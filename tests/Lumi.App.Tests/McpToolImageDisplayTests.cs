using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Lumi.Mcp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.App.Tests;

/// <summary>
/// MCP 工具回傳的圖片要顯示在回覆裡：真的 SQLite、真的附件資料夾，只有供應商是腳本化的。
/// 走過「MCP 結果 → 存附件 → 串流事件／重開對話 → 回覆附件」這一整條路。
/// </summary>
public sealed class McpToolImageDisplayTests : IDisposable
{
    private const string McpToolName = "mcp__computer__get_app_state";
    private const string ImageName = "MCP 圖片 1.png";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-mcp-images-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConversationStore _db;
    private readonly FileAttachmentStore _attachments;

    public McpToolImageDisplayTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = Path.Combine(_directory, "lumi.db") }));
        _attachments = new FileAttachmentStore(Options.Create(new AttachmentOptions { Directory = Path.Combine(_directory, "attachments") }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class NoopResponder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private ToolCardViewModel NewCard(string toolName, bool withStore = true) => new("c1", toolName, toolName, ToolPermission.ReadOnly, null, "{}",
        new NoopResponder(), new FakeClipboardService(), new FakeTimeProvider(), attachments: withStore ? _attachments : null);

    private static ToolResultBlock Result(params ImageBlock[] images) =>
        new("c1", "結果", false, "工具", null, "完成", 3, ToolApproval.AllowedOnce, images);

    /// <summary>用真的對話迴圈跑一次 MCP 工具，回傳對話 id 與結果區塊。</summary>
    private async Task<(string ConversationId, ToolResultBlock Block)> RunMcpToolAsync()
    {
        var tool = new LambdaTool(McpToolName, "取得畫面")
        {
            Behavior = (_, _) => Task.FromResult(McpResultFormatter.Format("電腦", "get_app_state",
                new McpCallResult(false, [new McpTextPart("App=Finder"), new McpImagePart("image/png", Png)]))),
        };
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, new ToolUseBlock("c1", McpToolName, """{"text":"x"}""")),
            SequenceChatProvider.Answer("畫面上是 Finder。"));
        var service = new ConversationService(provider, _db, new FakeModelCatalog(), new ToolRegistry([tool]), new ToolApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance, attachments: _attachments);
        var conversation = await _db.CreateAsync("測試");

        ToolResultBlock? block = null;
        await foreach (var e in service.SendAsync(conversation.Id, "看畫面"))
        {
            if (e is ToolCallCompleted completed)
                block = completed.Result;
        }
        return (conversation.Id, Assert.IsType<ToolResultBlock>(block));
    }

    [Fact]
    public async Task McpImage_IsSaved_AndShownAsAReplyAttachment()
    {
        var (_, block) = await RunMcpToolAsync();

        var card = NewCard(McpToolName);
        card.Complete(block);

        var image = Assert.IsType<ImageAttachmentViewModel>(Assert.Single(card.ReplyAttachments));
        Assert.True(image.ShowsImage);
        Assert.Equal(Png, await File.ReadAllBytesAsync(image.FilePath!));
        Assert.Equal(ImageName, image.FileName);   // 不是附件資料夾裡的雜湊檔名
    }

    [Fact]
    public async Task McpImage_IsStillShown_AfterReopeningTheConversation()
    {
        var (conversationId, _) = await RunMcpToolAsync();

        var items = ConversationTimeline.Build(await _db.GetMessagesAsync(conversationId), use => NewCard(use.Name));

        var image = Assert.IsType<ImageAttachmentViewModel>(Assert.Single(items.OfType<ToolRunViewModel>().Single().Cards.Single().ReplyAttachments));
        Assert.True(image.ShowsImage);
    }

    [Fact]
    public async Task BrowserScreenshot_StaysHiddenFromTheConversation()
    {
        var fileName = await _attachments.SaveAsync(Png, "image/png");
        var card = NewCard("browser_screenshot");

        card.Complete(Result(new ImageBlock(fileName, "image/png", "網頁截圖.png")));

        Assert.Empty(card.ReplyAttachments);
    }

    [Fact]
    public async Task McpImage_WhoseFileIsGone_ShowsTheMissingPlaceholder_WithItsReadableName()
    {
        var fileName = await _attachments.SaveAsync(Png, "image/png");
        File.Delete(_attachments.GetPath(fileName)!);
        var card = NewCard(McpToolName);

        card.Complete(Result(new ImageBlock(fileName, "image/png", ImageName)));

        var image = Assert.IsType<ImageAttachmentViewModel>(Assert.Single(card.ReplyAttachments));
        Assert.True(image.IsMissing);
        Assert.Equal(ImageName, image.FileName);
    }

    [Theory]
    [InlineData("../../secret.png", true)]   // 被改過的資料庫：檔名不合法，不能跳出附件資料夾
    [InlineData("a.png", false)]             // 沒有附件儲存
    public void McpImage_WithoutAResolvablePath_ShowsTheMissingPlaceholder(string fileName, bool withStore)
    {
        var card = NewCard(McpToolName, withStore);

        card.Complete(Result(new ImageBlock(fileName, "image/png", ImageName)));

        var image = Assert.IsType<ImageAttachmentViewModel>(Assert.Single(card.ReplyAttachments));
        Assert.True(image.IsMissing);
        Assert.False(image.ShowsImage);
    }
}
