using Lumi.Core.Chat;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Usage;

namespace Lumi.Core.Tests.Usage;

/// <summary>預算提醒的判斷與狀態保存（plan 第 5 節）。預算 25 美元：80% ＝ 20，100% ＝ 25。</summary>
public sealed class BudgetMonitorTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");

    private static DateTimeOffset At(int month, int day, int hour = 12, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    private static UsageRecord Rec(DateTimeOffset at, decimal cost) =>
        new(UsageRecord.NewId(), at, "conv", UsagePurpose.Chat, ChatMode.Workbench, "ms", "model", "conn",
            TokenUsage.Zero, false, null, cost, 0);

    /// <summary>可以同時指定本地時區的時鐘（<see cref="TimeProvider"/> 的 <c>LocalTimeZone</c> 預設是機器時區，測試要固定）。</summary>
    private sealed class ZonedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => Taipei;
    }

    private sealed class Fixture
    {
        public InMemoryUsageStore Usage { get; } = new();

        public InMemoryConversationStore Meta { get; } = new();

        public FakeModelCatalog Catalog { get; } = new();

        public ZonedClock Clock { get; } = new(At(10, 12));

        public BudgetMonitor Monitor { get; }

        public Fixture()
        {
            Monitor = new BudgetMonitor(Usage, Meta, Catalog, Clock);
        }

        public Task<string?> AlertState() => Meta.GetMetaAsync(BudgetMonitor.BudgetAlertedKey);
    }

    private static async Task<Fixture> WithBudgetAsync(decimal budgetUsd = 25m)
    {
        var fx = new Fixture();
        await fx.Catalog.SetUsageAsync(new UsageSettings(budgetUsd));
        return fx;
    }

    [Fact]
    public async Task BelowEighty_NoAlert()
    {
        var fx = await WithBudgetAsync();
        fx.Usage.Records.Add(Rec(At(10, 2), 19m)); // 76%

        Assert.Null(await fx.Monitor.CheckAsync());
        Assert.Null(await fx.AlertState());
    }

    [Fact]
    public async Task CrossingEighty_AlertsOnce_ThenSilent()
    {
        var fx = await WithBudgetAsync();
        fx.Usage.Records.Add(Rec(At(10, 2), 20m)); // 恰好 80%

        Assert.Equal(BudgetAlertLevel.Eighty, await fx.Monitor.CheckAsync());
        Assert.Equal("2026-10:80", await fx.AlertState());

        Assert.Null(await fx.Monitor.CheckAsync());
    }

    [Fact]
    public async Task CrossingHundredDirectly_AlertsOnlyHundred()
    {
        var fx = await WithBudgetAsync();
        fx.Usage.Records.Add(Rec(At(10, 2), 30m)); // 120%，兩個門檻一起跨過

        Assert.Equal(BudgetAlertLevel.Hundred, await fx.Monitor.CheckAsync());
        Assert.Equal("2026-10:100", await fx.AlertState());

        Assert.Null(await fx.Monitor.CheckAsync());
    }

    [Fact]
    public async Task EightyThenHundred_AlertsHundredOnce()
    {
        var fx = await WithBudgetAsync();
        fx.Usage.Records.Add(Rec(At(10, 2), 20m));
        Assert.Equal(BudgetAlertLevel.Eighty, await fx.Monitor.CheckAsync());

        fx.Usage.Records.Add(Rec(At(10, 3), 5m));
        Assert.Equal(BudgetAlertLevel.Hundred, await fx.Monitor.CheckAsync());
        Assert.Equal("2026-10:100", await fx.AlertState());

        Assert.Null(await fx.Monitor.CheckAsync());
    }

    [Fact]
    public async Task AfterHundred_EightyIsNotAlertedAgain()
    {
        var fx = await WithBudgetAsync();
        await fx.Meta.SetMetaAsync(BudgetMonitor.BudgetAlertedKey, "2026-10:100");
        fx.Usage.Records.Add(Rec(At(10, 2), 21m)); // 84%，已經提醒過 100%

        Assert.Null(await fx.Monitor.CheckAsync());
    }

    [Fact]
    public async Task NewMonth_ResetsAlert()
    {
        var fx = await WithBudgetAsync();
        await fx.Meta.SetMetaAsync(BudgetMonitor.BudgetAlertedKey, "2026-10:100");
        fx.Usage.Records.Add(Rec(At(10, 20), 30m)); // 十月的花費不算十一月
        fx.Clock.Now = At(11, 2);
        fx.Usage.Records.Add(Rec(At(11, 2), 21m));  // 十一月 84%

        Assert.Equal(BudgetAlertLevel.Eighty, await fx.Monitor.CheckAsync());
        Assert.Equal("2026-11:80", await fx.AlertState());
    }

    [Fact]
    public async Task UsesLocalMonth_NotUtc()
    {
        var fx = await WithBudgetAsync();
        // 九月 30 日 23:30（台北）＝ 九月，不算十月。
        fx.Usage.Records.Add(Rec(new DateTimeOffset(2026, 9, 30, 23, 30, 0, TimeSpan.FromHours(8)), 100m));
        // 十月 1 日 00:30（台北）＝ 十月，UTC 是 9/30 16:30。
        fx.Usage.Records.Add(Rec(At(10, 1, 0, 30), 20m));

        Assert.Equal(BudgetAlertLevel.Eighty, await fx.Monitor.CheckAsync());
    }

    [Fact]
    public async Task NoBudget_NoAlert()
    {
        var fx = new Fixture();
        fx.Usage.Records.Add(Rec(At(10, 2), 1000m));

        Assert.Null(await fx.Monitor.CheckAsync());
        Assert.Null(await fx.AlertState());
    }

    [Fact]
    public async Task ZeroBudget_NoAlert()
    {
        var fx = await WithBudgetAsync(0m);
        fx.Usage.Records.Add(Rec(At(10, 2), 1000m));

        Assert.Null(await fx.Monitor.CheckAsync());
        Assert.Null(await fx.AlertState());
    }

    [Fact]
    public async Task GetMonth_ReturnsSpentAndBudget()
    {
        var fx = await WithBudgetAsync();
        fx.Usage.Records.Add(Rec(At(10, 2), 12m));
        fx.Usage.Records.Add(Rec(At(9, 30), 99m)); // 上個月

        var (spent, budget) = await fx.Monitor.GetMonthAsync();

        Assert.Equal(12m, spent);
        Assert.Equal(25m, budget);
    }

    [Fact]
    public async Task GetMonth_BudgetIsNull_WhenNotSet()
    {
        var fx = new Fixture();

        var (spent, budget) = await fx.Monitor.GetMonthAsync();

        Assert.Equal(0m, spent);
        Assert.Null(budget);
    }
}
