using Lumi.App.Tests.TestSupport;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Prompts;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Infrastructure.Persistence;
using Lumi.Tools;
using Lumi.Tools.Notes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.App.Tests;

/// <summary>
/// M26 的整合測試：真的 SQLite、真的 prompt 層與 <see cref="ReadConversationTool"/>，只有供應商是腳本化的。
/// 驗證「舊對話 → 新對話帶入近況 → 模型用清單裡的 id 讀回 → 重開對話」這一整條路，以及畫面、網址來源都看不到清單。
/// </summary>
public sealed class RecentConversationsEndToEndTests : IDisposable
{
    private const string ToolSecret = "工具結果原文不該外流";
    private const string FileSecret = "檔案內容不該外流";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConversationStore _db;
    private readonly FakeModelCatalog _catalog = new();

    public RecentConversationsEndToEndTests()
    {
        Directory.CreateDirectory(_directory);
        _db = new SqliteConversationStore(Options.Create(new PersistenceOptions { DatabasePath = Path.Combine(_directory, "lumi.db") }));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private ConversationService Service(IChatProvider provider) => new(
        provider, _db, _catalog, new ToolRegistry([new ReadConversationTool(_db)]),
        new ToolApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
        composer: new PromptComposer([new BasePromptLayer(), new RecentConversationsPromptLayer()]));

    private static async Task<List<ChatEvent>> Run(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    /// <summary>一則做過事的舊對話：有檔案、有工具結果、有網址。</summary>
    private async Task<Conversation> SeedOldConversationAsync()
    {
        var old = await _db.CreateAsync("整理 Q3 報價單");
        var now = DateTimeOffset.UtcNow;
        await _db.AppendMessageAsync(old.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User,
            [new ContextBlock(ContextKind.Folder, "報價", "/Users/x/報價", FileSecret, true), new TextBlock("把三份報價合併成一張表")], now));
        await _db.AppendMessageAsync(old.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new ToolUseBlock("t1", "write_file", "{}")], now));
        await _db.AppendMessageAsync(old.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Tool,
            [new ToolResultBlock("t1", ToolSecret, false, "寫入檔案", "合併.xlsx", "已寫入", 1, ToolApproval.NotRequired)], now));
        await _db.AppendMessageAsync(old.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant,
            [new TextBlock("已合併，來源在 https://never-opened.example/price")], now));
        return (await _db.GetConversationAsync(old.Id))!;
    }

    private static string PromptOf(ChatMessage message) => ContextBlockFormatter.ToPromptText(message.Content);

    [Fact]
    public async Task NewConversation_GetsTheDigest_AndTheModelCanReadTheOldConversationById()
    {
        var old = await SeedOldConversationAsync();
        var current = await _db.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(null, new ToolUseBlock("r1", "read_conversation", $$"""{"conversation_id":"{{old.Id}}"}""")),
            SequenceChatProvider.Answer("好"));

        var events = await Run(Service(provider).SendAsync(current.Id, "接續剛才的"));

        // 第一次請求：system 有規則，第一則訊息有清單，且清單裡的 id 就是舊對話
        var first = provider.Requests[0];
        Assert.Contains("只有相關才提及", first.System);
        var prompt = PromptOf(first.Messages[0]);
        Assert.Contains($"id={old.Id}", prompt);
        Assert.Contains("整理 Q3 報價單", prompt);
        Assert.Contains("把三份報價合併成一張表", prompt);
        Assert.Contains("寫入檔案 · 合併.xlsx · 已寫入", prompt);
        Assert.Contains("/Users/x/報價", prompt);
        Assert.EndsWith("接續剛才的", prompt);
        Assert.DoesNotContain(ToolSecret, prompt);
        Assert.DoesNotContain(FileSecret, prompt);

        // 模型用那個 id 讀回：有內容，但沒有工具結果原文與檔案內容
        var result = Assert.Single(events.OfType<ToolCallCompleted>()).Result;
        Assert.False(result.IsError);
        Assert.Contains("把三份報價合併成一張表", result.Content);
        Assert.Contains("寫入檔案 · 合併.xlsx · 已寫入", result.Content);
        Assert.DoesNotContain(ToolSecret, result.Content);
        Assert.DoesNotContain(FileSecret, result.Content);
    }

    [Fact]
    public async Task ReopenedConversation_ShowsNoChip_AndTheDigestIsNotAUrlSource_AndIsNotAddedAgain()
    {
        await SeedOldConversationAsync();
        var current = await _db.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"), SequenceChatProvider.Answer("再一次"));
        var service = Service(provider);
        await Run(service.SendAsync(current.Id, "你好"));
        await Run(service.SendAsync(current.Id, "第二句"));

        var history = await _db.GetMessagesAsync(current.Id);

        // 存檔還原後種類正確，只有第一則有清單
        var blocks = history.SelectMany(m => m.Content).OfType<ContextBlock>().ToList();
        Assert.Equal(ContextKind.RecentConversations, Assert.Single(blocks).Kind);
        // 使用者看不到
        Assert.All(history.Where(m => m.Role == ChatRole.User), m => Assert.False(MessageViewModel.From(m).HasAttachments));
        Assert.Equal("你好", MessageViewModel.From(history[0]).Text);
        // 清單裡的網址不算使用者提供過
        Assert.Contains("never-opened.example", blocks[0].Content);
        Assert.False(UrlProvenance.From(history, []).IsKnown(new Uri("https://never-opened.example/price")));
    }

    [Fact]
    public async Task TurnedOff_NoDigestAndNoRule()
    {
        await SeedOldConversationAsync();
        await _catalog.SetRecentConversationsEnabledAsync(false);
        var current = await _db.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Run(Service(provider).SendAsync(current.Id, "你好"));

        Assert.Empty(provider.Requests[0].Messages[0].Content.OfType<ContextBlock>());
        Assert.DoesNotContain("只有相關才提及", provider.Requests[0].System);
    }

    [Fact]
    public async Task FirstConversationEver_HasNoDigest()
    {
        var current = await _db.CreateAsync("第一則");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Run(Service(provider).SendAsync(current.Id, "你好"));

        Assert.Empty(provider.Requests[0].Messages[0].Content.OfType<ContextBlock>());
    }
}
