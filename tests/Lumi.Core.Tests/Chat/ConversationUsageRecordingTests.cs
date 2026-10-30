using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Chat;

/// <summary>M23 23B：每次供應商呼叫都記一筆用量（第 3 節的估算規則、摘要呼叫、模式、單價快照）。</summary>
public class ConversationUsageRecordingTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public InMemoryUsageStore Usage { get; } = new();
        public Conversation Conversation { get; private set; } = null!;
        public ConversationService Service { get; private set; } = null!;

        public static async Task<Rig> CreateAsync(
            IChatProvider provider, FakeModelCatalog? catalog = null, IEnumerable<ITool>? tools = null, IUsageRecorder? recorder = null)
        {
            var rig = new Rig();
            rig.Conversation = await rig.Store.CreateAsync("新對話");
            rig.Service = new ConversationService(provider, rig.Store, catalog ?? new FakeModelCatalog(), new ToolRegistry(tools ?? []),
                new ScriptedApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(),
                TimeProvider.System, NullLogger<ConversationService>.Instance,
                usageRecorder: recorder ?? new UsageRecorder(rig.Usage, TimeProvider.System, NullLogger<UsageRecorder>.Instance));
            return rig;
        }
    }

    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    private static ChatMessage AssistantWithoutUsage(string text) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow);

    /// <summary>和 <c>ConversationService.EstimateRequestInput</c> 同一套規則（測試不能直接呼叫 internal）。</summary>
    private static int ExpectedInput(ChatRequest request) =>
        ContextEstimator.EstimateText(request.System)
        + (request.Tools is { Count: > 0 } tools ? ContextEstimator.EstimateText(ToolSnapshotJson.Serialize(tools)) : 0)
        + request.Messages.Sum(ContextEstimator.EstimateMessage);

    private static ChatEvent[] Turn(params ChatEvent[] events) => events;

    [Fact]
    public async Task Completed_WithUsage_RecordsTheActualUsage()
    {
        var rig = await Rig.CreateAsync(new SequenceChatProvider(SequenceChatProvider.Answer("好")));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var record = Assert.Single(rig.Usage.Records);
        Assert.Equal(new TokenUsage(10, 5, 0, 0), record.Tokens);   // ScriptedChatProvider.Assistant 的用量
        Assert.False(record.Estimated);
        Assert.Equal(rig.Conversation.Id, record.ConversationId);
        Assert.Equal(UsagePurpose.Chat, record.Purpose);
        Assert.Equal(ChatMode.Workbench, record.Mode);
        Assert.Equal("test-model", record.ModelId);
        Assert.Null(record.Price);   // 測試模型沒有單價：快照與費用都是 null
        Assert.Null(record.CostUsd);
    }

    [Fact]
    public async Task Completed_WithoutMessageUsage_UsesLastUsageEvent()
    {
        var provider = new SequenceChatProvider(
            Turn(new UsageEvent(new TokenUsage(7, 3, 2, 1)), new TextDelta("好"), new Completed(AssistantWithoutUsage("好"), StopReason.EndTurn)));
        var rig = await Rig.CreateAsync(provider);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var record = Assert.Single(rig.Usage.Records);
        Assert.Equal(new TokenUsage(7, 3, 2, 1), record.Tokens);
        Assert.False(record.Estimated);
    }

    [Fact]
    public async Task Completed_NoUsageAtAll_RecordsEstimated()
    {
        var provider = new SequenceChatProvider(Turn(new TextDelta("你好世界"), new Completed(AssistantWithoutUsage("你好世界"), StopReason.EndTurn)));
        var rig = await Rig.CreateAsync(provider);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var record = Assert.Single(rig.Usage.Records);
        Assert.True(record.Estimated);
        Assert.Equal(ExpectedInput(provider.Requests[0]), record.Tokens.InputTokens);
        Assert.Equal(ContextEstimator.EstimateText("你好世界"), record.Tokens.OutputTokens);
        Assert.Equal(0, record.Tokens.CacheReadInputTokens);
        Assert.Equal(0, record.Tokens.CacheCreationInputTokens);
    }

    [Fact]
    public async Task ToolLoop_RecordsEachRequest_WithToolDefinitionTokens()
    {
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "echo", """{"text":"嗨"}""")), SequenceChatProvider.Answer("好"));
        var rig = await Rig.CreateAsync(provider, tools: [new FakeTool()]);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "x"));

        Assert.Equal(2, rig.Usage.Records.Count);
        Assert.All(rig.Usage.Records, r => Assert.Equal(UsagePurpose.Chat, r.Purpose));
        var expectedTools = ContextEstimator.EstimateText(ToolSnapshotJson.Serialize(provider.Requests[0].Tools!));
        Assert.All(rig.Usage.Records, r => Assert.Equal(expectedTools, r.ToolDefinitionTokens));
        Assert.True(expectedTools > 0);
    }

    [Fact]
    public async Task ToolsDisabled_ToolDefinitionTokensAreZero()
    {
        var catalog = new FakeModelCatalog
        {
            Current = new AppSettings(
                [FakeModelCatalog.Connection], [FakeModelCatalog.Model with { ToolsEnabled = false }],
                FakeModelCatalog.Model.Id, FakeModelCatalog.Model.Id, ThemePreference.System),
        };
        var rig = await Rig.CreateAsync(new SequenceChatProvider(SequenceChatProvider.Answer("好")), catalog, tools: [new FakeTool()]);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Equal(0, Assert.Single(rig.Usage.Records).ToolDefinitionTokens);
    }

    [Fact]
    public async Task Cancelled_AfterText_RecordsEstimated()
    {
        var provider = new ScriptedChatProvider(new TextDelta("部分回答")) { HangAfter = 1 };
        var rig = await Rig.CreateAsync(provider);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in rig.Service.SendAsync(rig.Conversation.Id, "嗨", cts.Token))
            {
                if (e is TextDelta)
                    cts.Cancel();
            }
        });

        var record = Assert.Single(rig.Usage.Records);
        Assert.True(record.Estimated);
        Assert.Equal(ContextEstimator.EstimateText("部分回答"), record.Tokens.OutputTokens);
        Assert.Equal(ExpectedInput(provider.Requests[0]), record.Tokens.InputTokens);
    }

    [Fact]
    public async Task Failed_BeforeAnyEvent_RecordsNothing()
    {
        var provider = new SequenceChatProvider([new Failed(new ChatFailure(ChatFailureKind.Network, "連不上", true))]);
        var rig = await Rig.CreateAsync(provider);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.Empty(rig.Usage.Records);
    }

    [Fact]
    public async Task Failed_AfterText_RecordsEstimated()
    {
        var provider = new SequenceChatProvider(Turn(new TextDelta("半"), new Failed(new ChatFailure(ChatFailureKind.StreamError, "中斷", true))));
        var rig = await Rig.CreateAsync(provider);

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        var record = Assert.Single(rig.Usage.Records);
        Assert.True(record.Estimated);
        Assert.Equal(ContextEstimator.EstimateText("半"), record.Tokens.OutputTokens);
    }

    [Fact]
    public async Task Regenerate_KeepsEarlierRecords()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("舊"), SequenceChatProvider.Answer("新"));
        var rig = await Rig.CreateAsync(provider);
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        await Collect(rig.Service.RegenerateAsync(rig.Conversation.Id));

        Assert.Equal(2, rig.Usage.Records.Count);
        Assert.All(rig.Usage.Records, r => Assert.Equal(rig.Conversation.Id, r.ConversationId));
    }

    [Fact]
    public async Task PriceChange_DoesNotChangeEarlierRecords()
    {
        var catalog = new FakeModelCatalog();
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("一"), SequenceChatProvider.Answer("二"));
        var rig = await Rig.CreateAsync(provider, catalog);
        catalog.Current = WithPricing(new ModelPricing(3m, 15m, Source: PriceSource.Suggested));
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        catalog.Current = WithPricing(new ModelPricing(100m, 100m, Source: PriceSource.Confirmed));
        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "再問"));

        Assert.Equal(2, rig.Usage.Records.Count);
        Assert.Equal(new PriceSnapshot(3m, 15m, 3m, 3m, IsFree: false), rig.Usage.Records[0].Price);
        Assert.Equal(new PriceSnapshot(100m, 100m, 100m, 100m, IsFree: false), rig.Usage.Records[1].Price);
        Assert.Equal((10 * 3m + 5 * 15m) / 1_000_000m, rig.Usage.Records[0].CostUsd);
    }

    [Fact]
    public async Task SpriteMode_RecordsSpriteMode()
    {
        var rig = await Rig.CreateAsync(new SequenceChatProvider(SequenceChatProvider.Answer("好")));

        await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨", mode: ChatMode.Sprite));

        Assert.Equal(ChatMode.Sprite, Assert.Single(rig.Usage.Records).Mode);
    }

    [Fact]
    public async Task Compaction_RecordsCompactionPurpose()
    {
        var rig = await Rig.CreateAsync(new SequenceChatProvider(SequenceChatProvider.Answer("摘要")));
        for (var i = 0; i < 3; i++)
        {
            await rig.Store.AppendMessageAsync(rig.Conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock($"問{i}")], DateTimeOffset.UtcNow));
            await rig.Store.AppendMessageAsync(rig.Conversation.Id, AssistantWithoutUsage($"答{i}"));
        }

        var events = await Collect(rig.Service.CompactAsync(rig.Conversation.Id));

        Assert.IsType<CompactionCompleted>(events[^1]);
        var record = Assert.Single(rig.Usage.Records);
        Assert.Equal(UsagePurpose.Compaction, record.Purpose);
        Assert.Equal(ChatMode.Workbench, record.Mode);
        Assert.Equal(0, record.ToolDefinitionTokens);
        Assert.Equal(new TokenUsage(10, 5, 0, 0), record.Tokens);
        Assert.False(record.Estimated);
    }

    [Fact]
    public async Task Compaction_FailureWithoutEvents_RecordsNothing()
    {
        var rig = await Rig.CreateAsync(new SequenceChatProvider([new Failed(new ChatFailure(ChatFailureKind.Network, "連不上", true))]));
        for (var i = 0; i < 3; i++)
        {
            await rig.Store.AppendMessageAsync(rig.Conversation.Id, new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock($"問{i}")], DateTimeOffset.UtcNow));
            await rig.Store.AppendMessageAsync(rig.Conversation.Id, AssistantWithoutUsage($"答{i}"));
        }

        await Collect(rig.Service.CompactAsync(rig.Conversation.Id));

        Assert.Empty(rig.Usage.Records);
    }

    [Fact]
    public async Task RecorderThrows_TurnStillCompletesAndSavesMessages()
    {
        var rig = await Rig.CreateAsync(new SequenceChatProvider(SequenceChatProvider.Answer("好")), recorder: new ThrowingRecorder());

        var events = await Collect(rig.Service.SendAsync(rig.Conversation.Id, "嗨"));

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(2, (await rig.Store.GetMessagesAsync(rig.Conversation.Id)).Count);
    }

    private static AppSettings WithPricing(ModelPricing pricing)
    {
        var model = FakeModelCatalog.Model with { Pricing = pricing };
        return new AppSettings([FakeModelCatalog.Connection], [model], model.Id, model.Id, ThemePreference.System);
    }

    private sealed class ThrowingRecorder : IUsageRecorder
    {
        public Task RecordAsync(UsageObservation observation, CancellationToken ct = default) => throw new IOException("磁碟滿了");
    }
}
