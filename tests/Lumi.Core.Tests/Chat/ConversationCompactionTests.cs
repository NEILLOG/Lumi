using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;

namespace Lumi.Core.Tests.Chat;

/// <summary>M22 22H：觸發時機（80%／70%）、LLM 摘要、超過上限自動整理重送、手動整理。</summary>
public class ConversationCompactionTests
{
    private static FakeModelCatalog CatalogWithWindow(int window)
    {
        var catalog = new FakeModelCatalog();
        var model = FakeModelCatalog.Model with { ContextWindow = window, MaxOutputTokens = 64_000 };
        catalog.Current = catalog.Current with { Models = [model] };
        return catalog;
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static string Chars(int tokens) => new('a', tokens * 3);

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow);

    /// <summary>一輪：使用者、（可選的工具呼叫與結果）、助理文字。沒有 usage，所以估算全靠字元。</summary>
    private static async Task AddTurn(InMemoryConversationStore store, string conversationId, int turn, int userTokens, int answerTokens, int toolResultTokens = 0)
    {
        await store.AppendMessageAsync(conversationId, Msg(ChatRole.User, new TextBlock(Chars(userTokens))));
        if (toolResultTokens > 0)
        {
            await store.AppendMessageAsync(conversationId, Msg(ChatRole.Assistant, new ThinkingBlock("想", "sig"), new ToolUseBlock($"u{turn}", "read_file", "{}")));
            await store.AppendMessageAsync(conversationId, Msg(ChatRole.Tool,
                new ToolResultBlock($"u{turn}", Chars(toolResultTokens), false, "讀取檔案", $"f{turn}.txt", "ok", 1, ToolApproval.NotRequired)));
        }
        await store.AppendMessageAsync(conversationId, Msg(ChatRole.Assistant, new TextBlock(Chars(answerTokens))));
    }

    private static ChatEvent[] Summary(string text = "這是摘要", TokenUsage? usage = null) =>
    [
        new UsageEvent(usage ?? new TokenUsage(300, 40, 0, 0)),
        new TextDelta(text),
        new Completed(ScriptedChatProvider.Assistant(new TextBlock(text)) with { Usage = usage ?? new TokenUsage(300, 40, 0, 0) }, StopReason.EndTurn),
    ];

    private static ChatEvent[] Fail(ChatFailureKind kind) => [new Failed(new ChatFailure(kind, "x", false))];

    [Fact]
    public async Task NoContextWindow_NeverCompacts()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 4; i++)
            await AddTurn(store, conversation.Id, i, 5000, 5000);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(0));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.DoesNotContain(events, e => e is CompactionStarted or CompactionCompleted or CompactionFailed);
        Assert.Single(provider.Requests);
        Assert.Equal(CompactionState.Empty, await store.GetCompactionStateAsync(conversation.Id));
    }

    [Fact]
    public async Task BelowEightyPercent_DoesNotCompact()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 100, 100);   // 約 600 token，上限 1000 的 60%
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.DoesNotContain(events, e => e is CompactionStarted);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task OverEightyPercent_ButLayerOneIsEnough_OnlyClearsOldToolResults()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        await AddTurn(store, conversation.Id, 1, 20, 20, toolResultTokens: 900);   // 舊的工具結果佔了大半
        await AddTurn(store, conversation.Id, 2, 20, 20);
        await AddTurn(store, conversation.Id, 3, 20, 20);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.Equal([CompactionKind.ToolResults], events.OfType<CompactionStarted>().Select(e => e.Kind));
        var completed = Assert.Single(events.OfType<CompactionCompleted>());
        Assert.Equal(CompactionKind.ToolResults, completed.Kind);
        Assert.Null(completed.Usage);
        Assert.Single(provider.Requests);   // 沒有摘要呼叫
        var sent = provider.Requests[0].Messages.SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.Equal("（較早的工具結果已省略：讀取檔案 · f1.txt）", sent.Content);

        var state = await store.GetCompactionStateAsync(conversation.Id);
        Assert.Equal(completed.UptoSeq, state.ToolResultsUptoSeq);
        Assert.Null(state.SummaryUptoSeq);
        // 存檔的訊息不變
        var stored = (await store.GetMessagesAsync(conversation.Id)).SelectMany(m => m.Content).OfType<ToolResultBlock>().Single();
        Assert.Equal(Chars(900), stored.Content);
    }

    [Fact]
    public async Task StillOverSeventyPercent_AfterLayerOne_SummarizesAndSendsTheSummary()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 150, 150);   // 約 900 token，沒有工具結果可清
        var provider = new SequenceChatProvider(Summary("使用者在整理資料"), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.Equal([CompactionKind.ToolResults, CompactionKind.Summary], events.OfType<CompactionStarted>().Select(e => e.Kind));
        var done = events.OfType<CompactionCompleted>().Single(e => e.Kind == CompactionKind.Summary);
        Assert.Equal(new TokenUsage(300, 40, 0, 0), done.Usage);
        Assert.Contains(events, e => e is UsageEvent { Tokens.InputTokens: 300 });   // 摘要那次呼叫的用量有發出
        Assert.DoesNotContain(events, e => e is TextDelta { Text: "使用者在整理資料" });   // 摘要不是助理回應，不轉發文字
        Assert.IsType<Completed>(events[^1]);

        // 摘要呼叫：不帶工具、不要 thinking、輸出上限 4000、最後一則是整理指示
        var summaryRequest = provider.Requests[0];
        Assert.Null(summaryRequest.Tools);
        Assert.False(summaryRequest.Model.Model.ShowThinking);
        Assert.Equal(4000, summaryRequest.EffectiveMaxOutputTokens);
        Assert.Equal(CompactionApplier.SummaryInstruction, Assert.IsType<TextBlock>(summaryRequest.Messages[^1].Content[^1]).Text);

        // 主要呼叫：最近兩輪原樣保留，前面換成摘要
        var main = provider.Requests[1];
        var first = Assert.IsType<TextBlock>(main.Messages[0].Content[0]);
        Assert.Contains("使用者在整理資料", first.Text);
        Assert.Equal(3, main.Messages.Count);   // 第 3 輪的使用者與助理，加上這次的使用者
        var state = await store.GetCompactionStateAsync(conversation.Id);
        Assert.Equal(done.UptoSeq, state.SummaryUptoSeq);
        Assert.Equal("使用者在整理資料", state.Summary);
        Assert.Equal(new TokenUsage(300, 40, 0, 0), await store.GetCompactionUsageAsync(conversation.Id));
    }

    [Fact]
    public async Task SummaryFailure_ReportsCompactionFailed_StoresNothing_AndTheTurnContinues()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 150, 150);
        var provider = new SequenceChatProvider(Fail(ChatFailureKind.Network), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        var failed = Assert.Single(events.OfType<CompactionFailed>());
        Assert.Equal(CompactionKind.Summary, failed.Kind);
        Assert.Equal(ChatFailureKind.Network, failed.Failure.Kind);
        Assert.DoesNotContain(events, e => e is CompactionCompleted { Kind: CompactionKind.Summary });
        Assert.DoesNotContain(events, e => e is Failed);
        Assert.IsType<Completed>(events[^1]);   // 回應照常進行
        var state = await store.GetCompactionStateAsync(conversation.Id);
        Assert.Null(state.SummaryUptoSeq);
        Assert.Equal(TokenUsage.Zero, await store.GetCompactionUsageAsync(conversation.Id));
    }

    [Fact]
    public async Task EmptySummary_CountsAsAFailure()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 150, 150);
        var provider = new SequenceChatProvider(
            [new Completed(ScriptedChatProvider.Assistant(new TextBlock("  ")), StopReason.EndTurn)],
            SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.Single(events.OfType<CompactionFailed>());
        Assert.Null((await store.GetCompactionStateAsync(conversation.Id)).SummaryUptoSeq);
    }

    [Fact]
    public async Task ContextTooLong_CompactsAndResendsOnce()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 2; i++)
            await AddTurn(store, conversation.Id, i, 10, 10);   // 遠低於門檻：估算不會觸發，是供應商說太長
        var provider = new SequenceChatProvider(Fail(ChatFailureKind.ContextTooLong), Summary(), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.Equal(3, provider.Requests.Count);
        Assert.DoesNotContain(events, e => e is Failed);   // 第一次的失敗不轉發
        Assert.Contains(events, e => e is CompactionCompleted { Kind: CompactionKind.Summary });
        Assert.IsType<Completed>(events[^1]);
        Assert.Contains("這是摘要", Assert.IsType<TextBlock>(provider.Requests[2].Messages[0].Content[0]).Text);
        // 重送的歷史比第一次短
        Assert.True(provider.Requests[2].Messages.Count < provider.Requests[0].Messages.Count);
    }

    [Fact]
    public async Task ContextTooLong_AgainAfterCompacting_IsReportedAsFailed_AskingForANewConversation()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 2; i++)
            await AddTurn(store, conversation.Id, i, 10, 10);
        var provider = new SequenceChatProvider(Fail(ChatFailureKind.ContextTooLong), Summary(), Fail(ChatFailureKind.ContextTooLong));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.Equal(3, provider.Requests.Count);   // 只重送一次
        var failed = Assert.IsType<Failed>(events[^1]);
        Assert.Equal(ChatFailureKind.ContextTooLong, failed.Failure.Kind);
        Assert.Contains("開新對話", failed.Failure.Message);
        Assert.Single(events.OfType<Failed>());
    }

    [Fact]
    public async Task ContextTooLong_WithNothingToSummarize_FailsRightAway()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        var provider = new SequenceChatProvider(Fail(ChatFailureKind.ContextTooLong));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "很長的一則"));

        Assert.Single(provider.Requests);
        var failed = Assert.IsType<Failed>(Assert.Single(events));
        Assert.Contains("開新對話", failed.Failure.Message);
    }

    [Fact]
    public async Task ContextTooLong_WhenTheSummaryFails_ReportsBoth()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 2; i++)
            await AddTurn(store, conversation.Id, i, 10, 10);
        var provider = new SequenceChatProvider(Fail(ChatFailureKind.ContextTooLong), Fail(ChatFailureKind.ContextTooLong));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問"));

        Assert.Single(events.OfType<CompactionFailed>());
        Assert.Equal(ChatFailureKind.ContextTooLong, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task SpriteMode_CompactsToo()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 150, 150);
        var provider = new SequenceChatProvider(Summary(), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "再問", mode: ChatMode.Sprite));

        Assert.Contains(events, e => e is CompactionCompleted { Kind: CompactionKind.Summary });
    }

    [Fact]
    public async Task ReportedUsage_DrivesTheTrigger_WhenAvailable()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 5, 5);   // 字元估算只有幾十 token
        // 最近一則助理訊息回報的用量已經是上限的 90%
        await store.AppendMessageAsync(conversation.Id, Msg(ChatRole.User, new TextBlock("再問一個")));
        await store.AppendMessageAsync(conversation.Id, Msg(ChatRole.Assistant, new TextBlock("好")) with { Usage = new TokenUsage(100, 10, 0, 800) });
        var provider = new SequenceChatProvider(Summary(), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        var events = await Collect(service.SendAsync(conversation.Id, "下一個"));

        Assert.Contains(events, e => e is CompactionStarted);
    }

    [Fact]
    public async Task AfterCompacting_UsageReportedBeforeIt_IsIgnored_SoTheNextTurnDoesNotCompactAgain()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 5, 5);
        // 最後一則助理訊息回報的用量很大（整理之前的請求大小）
        await store.AppendMessageAsync(conversation.Id, Msg(ChatRole.Assistant, new TextBlock("好")) with { Usage = new TokenUsage(0, 10, 0, 900) });
        var manual = new SequenceChatProvider(Summary());
        await Collect(new ConversationService(manual, store, CatalogWithWindow(1000)).CompactAsync(conversation.Id));

        var next = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var events = await Collect(new ConversationService(next, store, CatalogWithWindow(1000)).SendAsync(conversation.Id, "再來"));

        Assert.DoesNotContain(events, e => e is CompactionStarted);
    }

    [Fact]
    public async Task ThinkingBlocksBeforeTheLastUserMessage_AreNotSentAfterCompacting()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        await AddTurn(store, conversation.Id, 1, 20, 20, toolResultTokens: 900);
        await AddTurn(store, conversation.Id, 2, 20, 20, toolResultTokens: 10);
        await AddTurn(store, conversation.Id, 3, 20, 20, toolResultTokens: 10);
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        await Collect(service.SendAsync(conversation.Id, "再問"));

        var sent = provider.Requests[0].Messages;
        Assert.DoesNotContain(sent.SelectMany(m => m.Content), b => b is ThinkingBlock);
        Assert.Contains(sent.SelectMany(m => m.Content), b => b is ToolUseBlock);
    }

    // ---- 手動整理 ----

    [Fact]
    public async Task ManualCompact_SummarizesWithoutLookingAtTheThreshold()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 5, 5);
        var provider = new SequenceChatProvider(Summary("手動摘要"));
        var service = new ConversationService(provider, store, CatalogWithWindow(1_000_000));

        var events = await Collect(service.CompactAsync(conversation.Id));

        Assert.IsType<CompactionStarted>(events[0]);
        var done = Assert.IsType<CompactionCompleted>(events[^1]);
        Assert.Equal(CompactionKind.Summary, done.Kind);
        var state = await store.GetCompactionStateAsync(conversation.Id);
        Assert.Equal("手動摘要", state.Summary);
        Assert.Equal(done.UptoSeq, state.SummaryUptoSeq);
        // 手動整理之後馬上再整理：沒有新東西，什麼都不發也不呼叫模型
        var again = new SequenceChatProvider();
        Assert.Empty(await Collect(new ConversationService(again, store, CatalogWithWindow(1_000_000)).CompactAsync(conversation.Id)));
        Assert.Empty(again.Requests);
    }

    [Fact]
    public async Task ManualCompact_TooShortConversation_DoesNothing()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        await AddTurn(store, conversation.Id, 1, 5, 5);
        var provider = new SequenceChatProvider();
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));

        Assert.Empty(await Collect(service.CompactAsync(conversation.Id)));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task ManualCompact_Failure_StoresNothing()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        for (var i = 1; i <= 3; i++)
            await AddTurn(store, conversation.Id, i, 5, 5);
        var service = new ConversationService(new SequenceChatProvider(Fail(ChatFailureKind.RateLimited)), store, CatalogWithWindow(1000));

        var events = await Collect(service.CompactAsync(conversation.Id));

        Assert.IsType<CompactionFailed>(events[^1]);
        Assert.Equal(CompactionState.Empty, await store.GetCompactionStateAsync(conversation.Id));
    }

    [Fact]
    public async Task ManualCompact_NoModel_ReportsFailed()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        var service = new ConversationService(new SequenceChatProvider(), store, new FakeModelCatalog(withModel: false));

        var failed = Assert.IsType<CompactionFailed>(Assert.Single(await Collect(service.CompactAsync(conversation.Id))));

        Assert.Equal(ChatFailureKind.ModelNotConfigured, failed.Failure.Kind);
    }

    [Fact]
    public async Task ManualCompact_WhileAResponseIsRunning_IsRefused()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("c");
        var provider = new ScriptedChatProvider { HangAfter = 0 };
        var service = new ConversationService(provider, store, CatalogWithWindow(1000));
        using var cts = new CancellationTokenSource();
        var running = Collect(service.SendAsync(conversation.Id, "嗨", cts.Token));
        await Task.Delay(50);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Collect(service.CompactAsync(conversation.Id)));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }
}
