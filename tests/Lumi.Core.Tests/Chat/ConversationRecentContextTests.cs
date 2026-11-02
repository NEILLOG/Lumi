using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Prompts;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>近況清單的接線（M26 26A）：只在新對話的第一則、工作台、非排程、設定開啟時帶入；失敗不擋送出。</summary>
public class ConversationRecentContextTests
{
    private static readonly DateTimeOffset Planned = new(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(8));

    /// <summary>會在 <see cref="ListAsync(int, ConversationCursor?, CancellationToken)"/>、<see cref="GetMessagesAsync"/> 時製造故障的包裝。</summary>
    private sealed class FaultyStore(IConversationStore inner) : IConversationStore
    {
        public Exception? ListFault { get; init; }

        public string? MessagesFaultOn { get; init; }

        public CancellationTokenSource? CancelOnList { get; init; }

        public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => inner.CreateAsync(title, ct);
        public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => inner.GetLatestAsync(ct);

        public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default)
        {
            CancelOnList?.Cancel();
            ct.ThrowIfCancellationRequested();
            if (ListFault is not null)
                throw ListFault;
            return inner.ListAsync(limit, before, ct);
        }

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default)
        {
            if (conversationId == MessagesFaultOn)
                throw new IOException("讀取失敗");
            return inner.GetMessagesAsync(conversationId, ct);
        }

        public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => inner.AppendMessageAsync(conversationId, message, ct);
        public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => inner.RemoveLastMessageAsync(conversationId, ct);
        public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => inner.RenameAsync(conversationId, title, ct);
        public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default) => inner.SearchAsync(query, limit, ct);
        public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default) => inner.DeleteAsync(conversationId, ct);
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ConversationService Service(IChatProvider provider, IConversationStore store, FakeModelCatalog catalog) =>
        new(provider, store, catalog, new ToolRegistry([]), new ScriptedApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);

    /// <summary>建一則有文字的舊對話（要求＋最後回覆），回傳它的 id。</summary>
    private static async Task<string> SeedAsync(IConversationStore store, string title, string ask, string reply)
    {
        var conversation = await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock(ask)], DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(reply)], DateTimeOffset.UtcNow));
        return conversation.Id;
    }

    private static IEnumerable<ContextBlock> DigestBlocks(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Content.OfType<ContextBlock>()).Where(b => b.Kind == ContextKind.RecentConversations);

    [Fact]
    public async Task FirstMessage_CarriesDigest_BeforeAttachmentsAndText_AndReachesProvider()
    {
        var store = new InMemoryConversationStore();
        var oldId = await SeedAsync(store, "報價", "整理三份報價", "已合併");
        var target = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        await Collect(Service(provider, store, new FakeModelCatalog()).SendAsync(target.Id, "接續剛才的"));

        var stored = await store.GetMessagesAsync(target.Id);
        var content = stored[0].Content;
        var digest = Assert.IsType<ContextBlock>(content[0]);
        Assert.Equal(ContextKind.RecentConversations, digest.Kind);
        Assert.True(digest.Embedded);
        Assert.StartsWith("<recent_conversations>\n", digest.Content);
        Assert.Contains($"id={oldId} ·", digest.Content);
        Assert.Contains("要求：整理三份報價", digest.Content);
        Assert.Contains("最後回覆：已合併", digest.Content);
        Assert.DoesNotContain($"id={target.Id}", digest.Content);
        Assert.Equal("接續剛才的", Assert.IsType<TextBlock>(content[^1]).Text);

        // 送給供應商的文字含清單
        var request = Assert.Single(provider.Requests);
        Assert.Contains("<recent_conversations>", ContextBlockFormatter.ToPromptText(request.Messages[0].Content));
    }

    [Fact]
    public async Task SecondMessage_DoesNotCarryDigestAgain()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "報價", "整理三份報價", "已合併");
        var target = await store.CreateAsync("新對話");
        var service = Service(new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二")), store, new FakeModelCatalog());

        await Collect(service.SendAsync(target.Id, "第一句"));
        await Collect(service.SendAsync(target.Id, "第二句"));

        var stored = await store.GetMessagesAsync(target.Id);
        Assert.Single(DigestBlocks(stored));
        Assert.Empty(DigestBlocks([stored[2]]));
    }

    [Fact]
    public async Task SpriteMode_DoesNotCarry()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "報價", "整理三份報價", "已合併");
        var target = await store.CreateAsync("新對話");

        await Collect(Service(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, new FakeModelCatalog())
            .SendAsync(target.Id, "嗨", mode: ChatMode.Sprite));

        Assert.Empty(DigestBlocks(await store.GetMessagesAsync(target.Id)));
    }

    [Fact]
    public async Task ScheduledRun_DoesNotCarry()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "報價", "整理三份報價", "已合併");
        var target = await store.CreateAsync("排程");
        var run = new ConversationRunOptions(new ScheduledRunInfo("晨間整理", Planned, Planned));

        await Collect(Service(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, new FakeModelCatalog())
            .SendAsync(target.Id, "執行", options: run));

        Assert.Empty(DigestBlocks(await store.GetMessagesAsync(target.Id)));
    }

    [Fact]
    public async Task SettingOff_DoesNotCarry()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "報價", "整理三份報價", "已合併");
        var target = await store.CreateAsync("新對話");
        var catalog = new FakeModelCatalog();
        await catalog.SetRecentConversationsEnabledAsync(false);

        await Collect(Service(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog).SendAsync(target.Id, "嗨"));

        Assert.Empty(DigestBlocks(await store.GetMessagesAsync(target.Id)));
    }

    [Fact]
    public async Task NoUsableConversations_DoesNotCarry()
    {
        var store = new InMemoryConversationStore();
        var target = await store.CreateAsync("新對話");

        await Collect(Service(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, new FakeModelCatalog()).SendAsync(target.Id, "嗨"));

        var stored = await store.GetMessagesAsync(target.Id);
        Assert.Empty(DigestBlocks(stored));
        Assert.IsType<TextBlock>(Assert.Single(stored[0].Content));
    }

    [Fact]
    public async Task ListFailure_MessageStillSent_WithoutDigest()
    {
        var inner = new InMemoryConversationStore();
        await SeedAsync(inner, "報價", "整理三份報價", "已合併");
        var target = await inner.CreateAsync("新對話");
        var store = new FaultyStore(inner) { ListFault = new InvalidOperationException("資料庫壞了") };
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));

        var events = await Collect(Service(provider, store, new FakeModelCatalog()).SendAsync(target.Id, "嗨"));

        Assert.IsType<Completed>(events.Last());
        Assert.DoesNotContain(events, e => e is Failed);
        var stored = await inner.GetMessagesAsync(target.Id);
        Assert.Empty(DigestBlocks(stored));
        Assert.Equal("嗨", Assert.IsType<TextBlock>(stored[0].Content[^1]).Text);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task MessageReadFailure_MessageStillSent_WithoutDigest()
    {
        var inner = new InMemoryConversationStore();
        var oldId = await SeedAsync(inner, "報價", "整理三份報價", "已合併");
        var target = await inner.CreateAsync("新對話");
        var store = new FaultyStore(inner) { MessagesFaultOn = oldId };

        var events = await Collect(Service(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, new FakeModelCatalog())
            .SendAsync(target.Id, "嗨"));

        Assert.DoesNotContain(events, e => e is Failed);
        Assert.Empty(DigestBlocks(await inner.GetMessagesAsync(target.Id)));
    }

    [Fact]
    public async Task Cancellation_PropagatesAndMessageIsNotStored()
    {
        var inner = new InMemoryConversationStore();
        await SeedAsync(inner, "報價", "整理三份報價", "已合併");
        var target = await inner.CreateAsync("新對話");
        using var cts = new CancellationTokenSource();
        var store = new FaultyStore(inner) { CancelOnList = cts };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Collect(Service(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, new FakeModelCatalog())
                .SendAsync(target.Id, "嗨", cts.Token)));

        Assert.Empty(await inner.GetMessagesAsync(target.Id));
    }

    [Fact]
    public async Task RetryAndRegenerate_DoNotCarryAgain()
    {
        var store = new InMemoryConversationStore();
        await SeedAsync(store, "報價", "整理三份報價", "已合併");
        var target = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            [new Failed(new ChatFailure(ChatFailureKind.Network, "無法連線到 api.test。", true))],
            SequenceChatProvider.Answer("重試成功"),
            SequenceChatProvider.Answer("重新產生"));
        var service = Service(provider, store, new FakeModelCatalog());

        await Collect(service.SendAsync(target.Id, "嗨"));
        await Collect(service.RetryAsync(target.Id));
        await Collect(service.RegenerateAsync(target.Id));

        var stored = await store.GetMessagesAsync(target.Id);
        Assert.Single(DigestBlocks(stored));
        Assert.Equal(3, provider.Requests.Count);
    }
}
