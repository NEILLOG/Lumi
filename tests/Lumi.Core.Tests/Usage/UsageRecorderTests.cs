using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Usage;

/// <summary>M23 23B：<see cref="UsageRecorder"/>（單價快照、本機免費、吞掉例外）。</summary>
public sealed class UsageRecorderTests
{
    private static readonly ConnectionSettings LocalConnection = new("local", "本機", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");

    private static ResolvedModel Remote(ModelPricing pricing) =>
        new(FakeModelCatalog.Model with { Pricing = pricing }, FakeModelCatalog.Connection);

    private static UsageObservation Observation(ResolvedModel model, bool estimated = false) =>
        new("conversation-1", UsagePurpose.Chat, ChatMode.Sprite, model, new TokenUsage(1_000_000, 500_000, 0, 0), estimated, ToolDefinitionTokens: 321);

    [Fact]
    public async Task Records_SnapshotCostAndSettingsAtThatTime()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store, TimeProvider.System, NullLogger<UsageRecorder>.Instance);

        await recorder.RecordAsync(Observation(Remote(new ModelPricing(3m, 15m, Source: PriceSource.Suggested))));

        var record = Assert.Single(store.Records);
        Assert.Equal("conversation-1", record.ConversationId);
        Assert.Equal(UsagePurpose.Chat, record.Purpose);
        Assert.Equal(ChatMode.Sprite, record.Mode);
        Assert.Equal(FakeModelCatalog.Model.Id, record.ModelSettingsId);
        Assert.Equal("test-model", record.ModelId);
        Assert.Equal("測試", record.ConnectionName);
        Assert.Equal(new PriceSnapshot(3m, 15m, 3m, 3m, IsFree: false), record.Price);
        Assert.Equal(3m + 7.5m, record.CostUsd);   // 1M 輸入 × 3 + 0.5M 輸出 × 15
        Assert.False(record.Estimated);
        Assert.Equal(321, record.ToolDefinitionTokens);
        Assert.NotEqual(default, record.CreatedAt);
    }

    [Fact]
    public async Task Estimated_IsStoredAsGiven()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store, TimeProvider.System, NullLogger<UsageRecorder>.Instance);

        await recorder.RecordAsync(Observation(Remote(new ModelPricing(3m, 15m)), estimated: true));

        Assert.True(Assert.Single(store.Records).Estimated);
    }

    [Fact]
    public async Task UnpricedRemoteModel_RecordsNullSnapshotAndNullCost()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store, TimeProvider.System, NullLogger<UsageRecorder>.Instance);

        await recorder.RecordAsync(Observation(Remote(ModelPricing.None)));

        var record = Assert.Single(store.Records);
        Assert.Null(record.Price);
        Assert.Null(record.CostUsd);
    }

    [Fact]
    public async Task LocalConnection_WithoutPrice_IsFree()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store, TimeProvider.System, NullLogger<UsageRecorder>.Instance);
        var local = new ResolvedModel(FakeModelCatalog.Model with { Pricing = ModelPricing.None }, LocalConnection);

        await recorder.RecordAsync(Observation(local));

        var record = Assert.Single(store.Records);
        Assert.Equal(PriceSnapshot.From(new ModelPricing(null, null, IsFree: true)), record.Price);
        Assert.Equal(0m, record.CostUsd);
        Assert.Equal(1_000_000, record.Tokens.InputTokens);   // 用量照記
    }

    [Fact]
    public async Task IntranetGateway_WithoutPrice_IsNotFree()
    {
        // 公司內網的 Gateway 不是本機模型：沒設定單價就是「未設定」，不能被當成免費
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store, TimeProvider.System, NullLogger<UsageRecorder>.Instance);
        var gateway = new ConnectionSettings("gw", "公司 Gateway", ProviderFormat.OpenAICompatible, "http://10.1.2.3:8080/v1");

        await recorder.RecordAsync(Observation(new ResolvedModel(FakeModelCatalog.Model with { Pricing = ModelPricing.None }, gateway)));

        var record = Assert.Single(store.Records);
        Assert.Null(record.Price);
        Assert.Null(record.CostUsd);
    }

    [Fact]
    public async Task LocalConnection_WithExplicitPrice_UsesThePrice()
    {
        var store = new InMemoryUsageStore();
        var recorder = new UsageRecorder(store, TimeProvider.System, NullLogger<UsageRecorder>.Instance);
        var local = new ResolvedModel(FakeModelCatalog.Model with { Pricing = new ModelPricing(1m, 2m, Source: PriceSource.Confirmed) }, LocalConnection);

        await recorder.RecordAsync(Observation(local));

        Assert.Equal(1m + 1m, Assert.Single(store.Records).CostUsd);   // 1M 輸入 × 1 + 0.5M 輸出 × 2
    }

    [Fact]
    public async Task StoreThrows_ExceptionIsSwallowed()
    {
        var recorder = new UsageRecorder(new ThrowingUsageStore(), TimeProvider.System, NullLogger<UsageRecorder>.Instance);

        await recorder.RecordAsync(Observation(Remote(new ModelPricing(3m, 15m))));
    }

    /// <summary>寫入一定失敗的假儲存（只有 <see cref="AddAsync"/> 會被呼叫）。</summary>
    private sealed class ThrowingUsageStore : IUsageStore
    {
        public Task AddAsync(UsageRecord record, CancellationToken ct = default) => throw new IOException("磁碟滿了");
        public Task<IReadOnlyList<UsageRecord>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConversationCost> GetConversationCostAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConversationCostItem>> ListTopConversationsAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<decimal> SumCostAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
