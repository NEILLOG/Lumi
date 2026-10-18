using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Chat;

public class ConversationServiceTests
{
    private static readonly FakeModelCatalog Catalog = new();

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ChatMessage User(string text, MessageStatus status = MessageStatus.Complete) =>
        new(ChatMessage.NewId(), ChatRole.User, [new TextBlock(text)], DateTimeOffset.UtcNow, status);

    private static ChatMessage AssistantText(string text, MessageStatus status = MessageStatus.Complete) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow, status);

    [Fact]
    public async Task Send_SavesUserMessage_ThenAssistantOnCompleted()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var reply = ScriptedChatProvider.Assistant(new ThinkingBlock("想", "sig"), new TextBlock("你好"));
        var provider = new ScriptedChatProvider(new TextDelta("你好"), new Completed(reply, StopReason.EndTurn));
        var service = new ConversationService(provider, store, Catalog);

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.IsType<Completed>(events[^1]);
        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Equal("嗨", Assert.IsType<TextBlock>(messages[0].Content[0]).Text);
        Assert.Same(reply, messages[1]);   // thinking 與 signature 原樣保存
        var request = Assert.Single(provider.Requests);
        Assert.Equal(FakeModelCatalog.Model, request.Model.Model);
        Assert.Equal(FakeModelCatalog.Connection, request.Model.Connection);
    }

    [Fact]
    public async Task Send_FirstMessage_SetsTitle_LaterMessagesDoNot()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var service = new ConversationService(
            new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("ok")), StopReason.EndTurn)),
            store, Catalog);

        await Collect(service.SendAsync(conversation.Id, "第一個問題"));
        Assert.Equal("第一個問題", store.Get(conversation.Id).Title);

        await Collect(service.SendAsync(conversation.Id, "第二個問題"));
        Assert.Equal("第一個問題", store.Get(conversation.Id).Title);
    }

    [Fact]
    public async Task Send_Failed_DoesNotSaveAssistant()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var failure = new ChatFailure(ChatFailureKind.Overloaded, "過載", Retryable: true);
        var service = new ConversationService(
            new ScriptedChatProvider(new TextDelta("部分"), new Failed(failure)), store, Catalog);

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        Assert.IsType<Failed>(events[^1]);
        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(ChatRole.User, Assert.Single(messages).Role);
    }

    [Fact]
    public async Task Cancel_AfterText_SavesStoppedMessage_AndRethrows()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new ThinkingDelta("想一下"), new TextDelta("已經說了"), new TextDelta("一些"))
        {
            HangAfter = 3,
        };
        var service = new ConversationService(provider, store, Catalog);
        using var cts = new CancellationTokenSource();

        var received = new List<ChatEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in service.SendAsync(conversation.Id, "嗨", cts.Token))
            {
                received.Add(e);
                if (received.Count == 3)
                    await cts.CancelAsync();
            }
        });

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        var stopped = messages[1];
        Assert.Equal(MessageStatus.Stopped, stopped.Status);
        Assert.Equal(ChatRole.Assistant, stopped.Role);
        // 只保留文字；未完成的思考沒有 signature，不能存
        Assert.Equal("已經說了一些", Assert.IsType<TextBlock>(Assert.Single(stopped.Content)).Text);
    }

    [Fact]
    public async Task Cancel_BeforeAnyText_SavesNothingForAssistant()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new ThinkingDelta("想一下")) { HangAfter = 1 };
        var service = new ConversationService(provider, store, Catalog);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.SendAsync(conversation.Id, "嗨", cts.Token))
                await cts.CancelAsync();
        });

        Assert.Single(await store.GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public async Task Retry_DoesNotAddUserMessage_AndResendsHistory()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("上次失敗的問題"));
        var provider = new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("這次成功")), StopReason.EndTurn));
        var service = new ConversationService(provider, store, Catalog);

        await Collect(service.RetryAsync(conversation.Id));

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal("上次失敗的問題", Assert.IsType<TextBlock>(Assert.Single(Assert.Single(provider.Requests).Messages).Content[0]).Text);
    }

    [Fact]
    public async Task Retry_WhenLastMessageIsAssistant_Throws()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("問"));
        await store.AppendMessageAsync(conversation.Id, AssistantText("答"));
        var service = new ConversationService(new ScriptedChatProvider(), store, Catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.RetryAsync(conversation.Id)));
    }

    [Fact]
    public async Task Regenerate_ReplacesLastAssistantMessage_WithoutTouchingEarlierOnes()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var question = User("問");
        await store.AppendMessageAsync(conversation.Id, question);
        await store.AppendMessageAsync(conversation.Id, AssistantText("舊答案"));
        var provider = new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("新答案")), StopReason.EndTurn));
        var service = new ConversationService(provider, store, Catalog);

        var events = await Collect(service.RegenerateAsync(conversation.Id));

        Assert.IsType<Completed>(events[^1]);
        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(2, messages.Count);
        Assert.Same(question, messages[0]);
        Assert.Equal("新答案", Assert.IsType<TextBlock>(messages[1].Content[^1]).Text);
        // 送出的歷史只有使用者那則：舊答案已經不在了
        Assert.Equal(ChatRole.User, Assert.Single(Assert.Single(provider.Requests).Messages).Role);
    }

    [Fact]
    public async Task Regenerate_AlsoReplacesAStoppedAnswer()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("問"));
        await store.AppendMessageAsync(conversation.Id, AssistantText("說到一半", MessageStatus.Stopped));
        var service = new ConversationService(
            new ScriptedChatProvider(new Completed(ScriptedChatProvider.Assistant(new TextBlock("完整答案")), StopReason.EndTurn)), store, Catalog);

        await Collect(service.RegenerateAsync(conversation.Id));

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(MessageStatus.Complete, messages[^1].Status);
        Assert.Equal(2, messages.Count);
    }

    [Fact]
    public async Task Regenerate_WhenLastMessageIsUser_Throws_AndChangesNothing()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("問"));
        var service = new ConversationService(new ScriptedChatProvider(), store, Catalog);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.RegenerateAsync(conversation.Id)));

        Assert.Single(await store.GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public async Task Regenerate_WithoutModel_KeepsTheOldAnswer()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("問"));
        await store.AppendMessageAsync(conversation.Id, AssistantText("舊答案"));
        var service = new ConversationService(new ScriptedChatProvider(), store, new FakeModelCatalog(withModel: false));

        var events = await Collect(service.RegenerateAsync(conversation.Id));

        Assert.Equal(ChatFailureKind.ModelNotConfigured, Assert.IsType<Failed>(Assert.Single(events)).Failure.Kind);
        Assert.Equal(2, (await store.GetMessagesAsync(conversation.Id)).Count);
    }

    [Fact]
    public async Task Regenerate_WhenProviderFails_OldAnswerIsGone_ButQuestionStays_SoRetryWorks()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("問"));
        await store.AppendMessageAsync(conversation.Id, AssistantText("舊答案"));
        var failing = new ScriptedChatProvider(new Failed(new ChatFailure(ChatFailureKind.Overloaded, "過載", true)));
        var service = new ConversationService(failing, store, Catalog);

        await Collect(service.RegenerateAsync(conversation.Id));

        var messages = await store.GetMessagesAsync(conversation.Id);
        Assert.Equal(ChatRole.User, Assert.Single(messages).Role);   // 最後是使用者訊息 → 錯誤列的「重試」可用
    }

    [Fact]
    public async Task Regenerate_Cancelled_SavesStoppedText_LikeSend()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        await store.AppendMessageAsync(conversation.Id, User("問"));
        await store.AppendMessageAsync(conversation.Id, AssistantText("舊答案"));
        var provider = new ScriptedChatProvider(new TextDelta("新的一半")) { HangAfter = 1 };
        var service = new ConversationService(provider, store, Catalog);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.RegenerateAsync(conversation.Id, cts.Token))
                await cts.CancelAsync();
        });

        var last = (await store.GetMessagesAsync(conversation.Id))[^1];
        Assert.Equal(MessageStatus.Stopped, last.Status);
        Assert.Equal("新的一半", Assert.IsType<TextBlock>(last.Content[0]).Text);
    }

    [Fact]
    public async Task ConcurrentCallsOnSameConversation_AreRejected()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider(new TextDelta("a")) { HangAfter = 1 };
        var service = new ConversationService(provider, store, Catalog);
        using var cts = new CancellationTokenSource();

        await using var first = service.SendAsync(conversation.Id, "一", cts.Token).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(service.SendAsync(conversation.Id, "二")));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task NoModelConfigured_ProducesFailed_ButKeepsUserMessage()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("新對話");
        var provider = new ScriptedChatProvider();
        var service = new ConversationService(provider, store, new FakeModelCatalog(withModel: false));

        var events = await Collect(service.SendAsync(conversation.Id, "嗨"));

        var failed = Assert.IsType<Failed>(Assert.Single(events));
        Assert.Equal(ChatFailureKind.ModelNotConfigured, failed.Failure.Kind);
        Assert.Empty(provider.Requests);
        Assert.Single(await store.GetMessagesAsync(conversation.Id));
    }

    [Fact]
    public void BuildRequestMessages_SkipsEmptyAndFailed_AndMergesSameRole()
    {
        var history = new List<ChatMessage>
        {
            User("第一題"),
            AssistantText("", MessageStatus.Stopped),                 // 空的：略過
            User("沒等到回應又問了一題"),                              // 和上一則 user 合併
            AssistantText("失敗的部分", MessageStatus.Failed),          // 失敗：略過
            AssistantText("停止前說的話", MessageStatus.Stopped),      // 停止但有文字：保留
            User("第三題"),
        };

        var result = ConversationService.BuildRequestMessages(history);

        Assert.Equal(3, result.Count);
        Assert.Equal(ChatRole.User, result[0].Role);
        Assert.Equal(["第一題", "沒等到回應又問了一題"], result[0].Content.OfType<TextBlock>().Select(t => t.Text));
        Assert.Equal("停止前說的話", Assert.IsType<TextBlock>(Assert.Single(result[1].Content)).Text);
        Assert.Equal("第三題", Assert.IsType<TextBlock>(Assert.Single(result[2].Content)).Text);
        // 原本的歷史不受影響
        Assert.Single(history[0].Content);
    }

    [Theory]
    [InlineData("短標題", "短標題")]
    [InlineData("第一行\n第二行", "第一行 第二行")]
    [InlineData("一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十多出來", "一二三四五六七八九十一二三四五六七八九十一二三四五六七八九十…")]
    public void MakeTitle_TruncatesToThirtyGraphemes(string input, string expected) =>
        Assert.Equal(expected, ConversationService.MakeTitle(input));

    [Fact]
    public void MakeTitle_DoesNotSplitEmoji()
    {
        var input = new string('字', 29) + "❤️‍🔥" + "後面";

        var title = ConversationService.MakeTitle(input);

        Assert.EndsWith("❤️‍🔥…", title);
    }
}
