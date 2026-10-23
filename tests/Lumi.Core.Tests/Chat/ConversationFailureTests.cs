using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Network;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>
/// M9 9C：失敗文字的補充在 <see cref="ConversationService"/> 一處套用（錯誤列與小精靈氣泡都從這裡拿到同一份 <see cref="ChatFailure"/>），
/// 以及串流中途失敗時的存檔行為（現況鎖定，agent.md 第 7 節不改）。
/// </summary>
public class ConversationFailureTests
{
    private sealed class FixedNetwork(bool hasInterface) : INetworkStatus
    {
        public bool HasUsableNetworkInterface() => hasInterface;
    }

    private static ConversationService Service(IChatProvider provider, IConversationStore store, bool hasInterface, IModelCatalog? catalog = null) =>
        new(provider, store, catalog ?? new FakeModelCatalog(), new ToolRegistry([]), new DenyAllToolApprovalService(), new ToolPermissionMemory(),
            new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance,
            network: new FixedNetwork(hasInterface));

    private static FakeModelCatalog LocalCatalog()
    {
        var catalog = new FakeModelCatalog();
        var local = FakeModelCatalog.Connection with { BaseUrl = "http://localhost:11434/v1" };
        catalog.Current = catalog.Current with { Connections = [local] };
        return catalog;
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ChatMessage User(string text) => new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow);

    [Fact]
    public async Task NetworkFailure_WithoutAnyInterface_GetsTheOfflineSentence()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Network, "無法連線到 api.test。", true)));
        var service = Service(provider, store, hasInterface: false);

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var failed = Assert.IsType<Failed>(Assert.Single(events, e => e is Failed));
        Assert.Equal("無法連線到 api.test。看起來沒有網路連線。", failed.Failure.Message);
        Assert.Equal(ChatFailureKind.Network, failed.Failure.Kind);
    }

    [Fact]
    public async Task NetworkFailure_WithAnInterface_KeepsProviderMessage()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Network, "無法連線到 api.test。", true)));
        var service = Service(provider, store, hasInterface: true);

        var failed = Assert.IsType<Failed>(Assert.Single(await Collect(service.SendAsync(conversation.Id, "嗨"))));

        Assert.Equal("無法連線到 api.test。", failed.Failure.Message);
    }

    [Fact]
    public async Task NetworkFailure_LocalTarget_IsNotTouchedEvenWithoutInterfaces()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Network, "無法連線到 localhost，請確認服務是否已啟動。", true)));
        var service = Service(provider, store, hasInterface: false, catalog: LocalCatalog());

        var failed = Assert.IsType<Failed>(Assert.Single(await Collect(service.SendAsync(conversation.Id, "嗨"))));

        Assert.Equal("無法連線到 localhost，請確認服務是否已啟動。", failed.Failure.Message);
    }

    [Fact]
    public async Task RateLimited_WithRetryAfter_ShowsTheWait_AndKeepsTheFields()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, TimeSpan.FromSeconds(42), "req_1")));
        var service = Service(provider, store, hasInterface: true);

        var failed = Assert.IsType<Failed>(Assert.Single(await Collect(service.SendAsync(conversation.Id, "嗨"))));

        Assert.Equal("已達到速率限制，約 42 秒後再試。", failed.Failure.Message);
        Assert.Equal(TimeSpan.FromSeconds(42), failed.Failure.RetryAfter);
        Assert.Equal("req_1", failed.Failure.RequestId);
        Assert.True(failed.Failure.Retryable);
    }

    [Fact]
    public async Task TheDecoratedFailure_IsWhatEveryObserverAndTheRunResultSee()
    {
        // 錯誤列（工作台）與小精靈氣泡都從 ChatSession 拿到同一個 ChatFailure：文字在 ConversationService 一處決定
        var store = new InMemoryConversationStore();
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, TimeSpan.FromSeconds(30))));
        var session = new ChatSession(Service(provider, store, hasInterface: true), store);
        await session.InitializeAsync("新對話");
        var observed = new List<string>();
        session.EventReceived += (_, e) =>
        {
            if (e is Failed failed)
                observed.Add(failed.Failure.Message);
        };

        var result = await session.SendAsync("嗨");

        const string expected = "已達到速率限制，約 30 秒後再試。";
        Assert.Equal([expected], observed);
        Assert.Equal(expected, result.Failure?.Message);
    }

    [Fact]
    public async Task RetryAfter_IsNotAutomaticallyRetried()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.RateLimited, "已達到速率限制，請稍後再試。", true, TimeSpan.FromSeconds(1))));
        var service = Service(provider, store, hasInterface: true);

        await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.Single(provider.Requests);   // 只送一次：自動重試不做（plan/09「直接定的事」）
    }

    [Fact]
    public async Task StreamFailure_AfterSomeText_SavesNoAssistantMessage_AndRetryResendsTheSameQuestion()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new SequenceChatProvider(
            [new TextDelta("已經收到的一部分"), new Failed(new ChatFailure(ChatFailureKind.StreamError, "串流資料解析失敗。", true))],
            [new Completed(ScriptedChatProvider.Assistant(new TextBlock("完整回答")), StopReason.EndTurn)]);
        var service = Service(provider, store, hasInterface: true);

        await Collect(service.SendAsync(conversation.Id, "問題"));

        // 失敗：部分文字不存（Q15），沒有助理訊息，也沒有 Stopped 狀態的訊息
        var afterFailure = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(ChatRole.User, Assert.Single(afterFailure).Role);

        // 重試：不新增使用者訊息、不刪除任何東西，同一段歷史再送一次
        var retry = await Collect(service.RetryAsync(conversation.Id));

        Assert.IsType<Completed>(retry[^1]);
        var afterRetry = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal([ChatRole.User, ChatRole.Assistant], afterRetry.Select(m => m.Role).ToArray());
        Assert.All(afterRetry, m => Assert.Equal(MessageStatus.Complete, m.Status));
        Assert.Equal(2, provider.Requests.Count);
        var resent = Assert.Single(provider.Requests[1].Messages);
        Assert.Equal("問題", Assert.IsType<TextBlock>(resent.Content[^1]).Text);
    }

    [Fact]
    public async Task Regenerate_RemovesTheStoredAnswerOnly_FromTheTail()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var first = User("第一個問題");
        await store.AppendMessageAsync(conversation.Id, first);
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("第一個回答")], DateTimeOffset.UtcNow));
        await store.AppendMessageAsync(conversation.Id, User("第二個問題"));
        await store.AppendMessageAsync(conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("第二個回答")], DateTimeOffset.UtcNow));
        var provider = new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("新的第二個回答")), StopReason.EndTurn));
        var service = Service(provider, store, hasInterface: true);

        await Collect(service.RegenerateAsync(conversation.Id));

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(4, messages.Count);
        Assert.Equal("第一個回答", Assert.IsType<TextBlock>(messages[1].Content[0]).Text);   // 前面的訊息沒有變
        Assert.Equal("新的第二個回答", Assert.IsType<TextBlock>(messages[3].Content[0]).Text);
    }
}
