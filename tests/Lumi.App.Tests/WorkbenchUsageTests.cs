using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>工作台的本對話用量：送出、串流、完成、新對話、載入歷史、快滿、連線重試（ui-usage-and-topbar.md 第 4 節）。</summary>
public sealed class WorkbenchUsageTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; }
        public FakeModelCatalog Catalog { get; } = new();
        public ConnectionStatusCache Status { get; } = new();
        public WorkbenchViewModel Vm { get; }

        public Rig(SequenceChatProvider provider, FakeConnectionTester? tester = null, InMemoryConversationStore? store = null)
        {
            Store = store ?? new InMemoryConversationStore();
            var service = new ConversationService(provider, Store, Catalog);
            Vm = new WorkbenchViewModel(service, Store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                Status, new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, tester: tester);
        }

        public async Task SendAsync(string text = "問")
        {
            Vm.InputText = text;
            await Vm.SendCommand.ExecuteAsync(null);
        }
    }

    /// <summary>一則完成的回應，usage 是這次請求的最終值。</summary>
    private static ChatEvent[] Finished(int input, int output, int cacheRead = 0, string text = "好") =>
    [
        new TextDelta(text),
        new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UtcNow,
            ModelId: "m", Usage: new TokenUsage(input, output, 0, cacheRead)), StopReason.EndTurn),
    ];

    [Fact]
    public async Task AfterASend_ShowsTheCumulativeUsage_AndTheLastRequestAsContext()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();

        await rig.SendAsync();

        Assert.True(rig.Vm.Usage.HasData);
        Assert.Equal("20", rig.Vm.Usage.InputText);
        Assert.Equal("8", rig.Vm.Usage.OutputText);
        Assert.Equal("1", rig.Vm.Usage.TurnsText);
        Assert.Equal(28, rig.Vm.Usage.ContextUsed);
        Assert.Equal("28 / 200K", rig.Vm.Usage.UsedOfText);
        Assert.Equal(Strings.Usage_LessThanOnePercent, rig.Vm.Usage.PercentText);
        Assert.Equal(UsageLevel.Normal, rig.Vm.Usage.Level);
        Assert.Equal("測試模型", rig.Vm.Usage.ModelName);
    }

    [Fact]
    public async Task TwoTurns_AccumulateInputAndOutput_ButContextIsOnlyTheLastRequest()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("甲"), SequenceChatProvider.Answer("乙")));
        await rig.Vm.InitializeAsync();

        await rig.SendAsync("一");
        await rig.SendAsync("二");

        Assert.Equal("40", rig.Vm.Usage.InputText);
        Assert.Equal("16", rig.Vm.Usage.OutputText);
        Assert.Equal("2", rig.Vm.Usage.TurnsText);
        Assert.Equal(28, rig.Vm.Usage.ContextUsed);   // 最近一次：20 + 8，不是累計
    }

    [Fact]
    public async Task AToolUseRequest_IsCommittedWhenItCompletes()
    {
        // 工具迴圈中間的一次請求（stop_reason = tool_use）完成時就併入累計，不等整輪結束
        var toolUseOnly = new ChatEvent[]
        {
            new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [], DateTimeOffset.UtcNow,
                ModelId: "m", Usage: new TokenUsage(10, 5, 0, 0)), StopReason.ToolUse),
        };
        var rig = new Rig(new SequenceChatProvider(toolUseOnly));
        await rig.Vm.InitializeAsync();

        await rig.SendAsync();

        Assert.Equal("10", rig.Vm.Usage.InputText);
        Assert.Equal("5", rig.Vm.Usage.OutputText);
        Assert.False(rig.Vm.Usage.ShowCacheRing);
    }

    [Fact]
    public async Task WhileStreaming_TheInFlightRequest_IsCountedOnce_NotAddedUpPerEvent()
    {
        var streaming = new ChatEvent[]
        {
            new UsageEvent(new TokenUsage(20, 2, 0, 0)),
            new TextDelta("好"),
            new UsageEvent(new TokenUsage(20, 5, 0, 0)),
            new TextDelta("好啊"),
        };
        var rig = new Rig(new SequenceChatProvider(streaming) { HangOnCall = 1 });
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding && rig.Vm.Usage.OutputText == "5");

        Assert.Equal("20", rig.Vm.Usage.InputText);
        Assert.Equal("5", rig.Vm.Usage.OutputText);   // 不是 2 + 5 = 7
        Assert.Equal("0", rig.Vm.Usage.TurnsText);    // 還沒完成，回合還沒算上

        await rig.Vm.StopAndWaitAsync();
        await sending;
    }

    [Fact]
    public async Task OnCompletion_TheMessageUsageReplacesTheInFlightValue()
    {
        var rig = new Rig(new SequenceChatProvider(
        [
            new UsageEvent(new TokenUsage(20, 5, 0, 0)),
            new TextDelta("好"),
            new Completed(new ChatMessage(ChatMessage.NewId(), ChatRole.Assistant, [new TextBlock("好")], DateTimeOffset.UtcNow,
                ModelId: "m", Usage: new TokenUsage(20, 8, 0, 0)), StopReason.EndTurn),
        ]));
        await rig.Vm.InitializeAsync();

        await rig.SendAsync();

        Assert.Equal("8", rig.Vm.Usage.OutputText);   // 完成後併入最終值，進行中的 5 不再另外加
        Assert.Equal("1", rig.Vm.Usage.TurnsText);
    }

    [Fact]
    public async Task NewConversation_ResetsTheUsageToEmpty()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();
        await rig.SendAsync();

        await rig.Vm.NewConversationCommand.ExecuteAsync(null);

        Assert.False(rig.Vm.Usage.HasData);
        Assert.Equal("0", rig.Vm.Usage.InputText);
        Assert.Equal("0", rig.Vm.Usage.OutputText);
        Assert.Equal("0", rig.Vm.Usage.TurnsText);
        Assert.Equal(UsageLevel.Empty, rig.Vm.Usage.Level);
        Assert.Equal("0%", rig.Vm.Usage.PercentText);
    }

    [Fact]
    public async Task LoadingAConversation_SumsTheSavedUsageFromTheStore()
    {
        var store = new InMemoryConversationStore();
        var first = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store: store);
        await first.Vm.InitializeAsync();
        await first.SendAsync();

        var reopened = new Rig(new SequenceChatProvider(), store: store);
        await reopened.Vm.InitializeAsync();

        Assert.Equal("20", reopened.Vm.Usage.InputText);
        Assert.Equal("8", reopened.Vm.Usage.OutputText);
        Assert.Equal("1", reopened.Vm.Usage.TurnsText);
        Assert.Equal(28, reopened.Vm.Usage.ContextUsed);
    }

    [Fact]
    public async Task CacheReads_FeedTheCacheRatio_ForTheProvidersThatReportThem()
    {
        // 快取讀取 80 ＋ 非快取 20 ＝ 總輸入 100；命中 80%
        var rig = new Rig(new SequenceChatProvider(Finished(20, 8, cacheRead: 80)));
        await rig.Vm.InitializeAsync();

        await rig.SendAsync();

        Assert.True(rig.Vm.Usage.ShowCacheRing);
        Assert.Equal(String.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Usage_CacheFormat, "80"), rig.Vm.Usage.CacheRatioText);
        Assert.Equal("100", rig.Vm.Usage.InputText);
    }

    [Fact]
    public async Task ProviderWithoutCacheReports_ShowsNoCacheRing()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();

        await rig.SendAsync();

        Assert.False(rig.Vm.Usage.ShowCacheRing);
    }

    [Fact]
    public async Task ContextWindowZero_HidesThePercentage_ButKeepsTheNumbers()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();
        rig.Catalog.Current = rig.Catalog.Current with { Models = [FakeModelCatalog.Model with { ContextWindow = 0 }] };
        rig.Catalog.Raise();

        await rig.SendAsync();

        Assert.False(rig.Vm.Usage.HasContextLimit);
        Assert.Equal(UsageLevel.Empty, rig.Vm.Usage.Level);
        Assert.Equal("", rig.Vm.Usage.PercentText);
        Assert.Equal("20", rig.Vm.Usage.InputText);
        Assert.Equal("8", rig.Vm.Usage.OutputText);
    }

    [Fact]
    public async Task NearTheLimit_IsDanger()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")));
        await rig.Vm.InitializeAsync();
        rig.Catalog.Current = rig.Catalog.Current with { Models = [FakeModelCatalog.Model with { ContextWindow = 30 }] };
        rig.Catalog.Raise();

        await rig.SendAsync();   // 20 + 8 = 28 of 30

        Assert.Equal(UsageLevel.Danger, rig.Vm.Usage.Level);
        Assert.True(rig.Vm.Usage.IsNearLimit);
        Assert.Equal("93%", rig.Vm.Usage.PercentText);
    }

    [Fact]
    public async Task Retry_RunsTheConnectionTest_AndUpdatesTheStatus()
    {
        var tester = new FakeConnectionTester { Result = ConnectionTestResult.Ok(["m"]) };
        var rig = new Rig(new SequenceChatProvider(), tester);
        await rig.Vm.InitializeAsync();
        rig.Status.Mark(FakeModelCatalog.Connection.Id, ConnectionStatus.Failed);
        Assert.True(rig.Vm.Usage.ShowRetry);

        await rig.Vm.Usage.RetryCommand.ExecuteAsync(null);

        Assert.Single(tester.Calls);
        Assert.Equal(FakeModelCatalog.Connection.Id, tester.Calls[0].Connection.Id);
        Assert.Equal(ConnectionStatus.Ok, rig.Status.Get(FakeModelCatalog.Connection.Id).Status);
        Assert.False(rig.Vm.Usage.ShowRetry);
        Assert.True(rig.Vm.Usage.IsConnectionOk);
    }

    [Fact]
    public async Task Retry_StillFailing_KeepsTheFailedState()
    {
        var tester = new FakeConnectionTester { Result = FakeConnectionTester.Failure(Lumi.Core.Chat.ChatFailureKind.Network, "連不上") };
        var rig = new Rig(new SequenceChatProvider(), tester);
        await rig.Vm.InitializeAsync();
        rig.Status.Mark(FakeModelCatalog.Connection.Id, ConnectionStatus.Failed);

        await rig.Vm.Usage.RetryCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.Usage.ShowRetry);
    }
}
