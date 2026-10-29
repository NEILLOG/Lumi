using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Updates;

public sealed class UpdateServiceTests
{
    private sealed class FakeChecker : IUpdateChecker
    {
        public UpdateInfo? Latest { get; set; }

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public Task<UpdateInfo?> GetLatestAsync(CancellationToken ct)
        {
            Calls++;
            return Failure is null ? Task.FromResult(Latest) : Task.FromException<UpdateInfo?>(Failure);
        }
    }

    private static UpdateInfo Release(string version) =>
        new(version, $"Lumi {version}", "修正問題", new Uri($"https://github.com/NEILLOG/DesktopAgent/releases/tag/v{version}"), null);

    private static string Newer => NextVersion(1);

    private static string NextVersion(int bump)
    {
        Assert.True(AppVersion.TryParse(AppVersion.Current, out var current));
        return $"{current.Major + bump}.0.0";
    }

    private static (UpdateService Service, FakeChecker Checker, FakeModelCatalog Catalog) Create()
    {
        var checker = new FakeChecker();
        var catalog = new FakeModelCatalog();
        return (new UpdateService(checker, catalog, NullLogger<UpdateService>.Instance), checker, catalog);
    }

    [Fact]
    public async Task ANewerRelease_IsAvailable_AndAnnounced()
    {
        var (service, checker, _) = Create();
        checker.Latest = Release(Newer);
        var announced = 0;
        service.AvailableChanged += (_, _) => announced++;

        var result = await service.CheckAsync(manual: false);

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal(Newer, service.Available?.Version);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task TheSameVersion_IsUpToDate()
    {
        var (service, checker, _) = Create();
        checker.Latest = Release(AppVersion.Current);

        var result = await service.CheckAsync(manual: false);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(service.Available);
    }

    [Fact]
    public async Task NoReleaseYet_IsUpToDate()
    {
        var (service, checker, _) = Create();
        checker.Latest = null;

        Assert.Equal(UpdateCheckOutcome.UpToDate, (await service.CheckAsync(manual: true)).Outcome);
    }

    [Fact]
    public async Task AutomaticCheck_DoesNothing_WhenTurnedOff()
    {
        var (service, checker, catalog) = Create();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(CheckForUpdates: false) };
        checker.Latest = Release(Newer);

        var result = await service.CheckAsync(manual: false);

        Assert.Equal(UpdateCheckOutcome.Disabled, result.Outcome);
        Assert.Equal(0, checker.Calls);
        Assert.Null(service.Available);
    }

    [Fact]
    public async Task ManualCheck_Works_EvenWhenTurnedOff()
    {
        var (service, checker, catalog) = Create();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(CheckForUpdates: false) };
        checker.Latest = Release(Newer);

        Assert.Equal(UpdateCheckOutcome.Available, (await service.CheckAsync(manual: true)).Outcome);
    }

    [Fact]
    public async Task ASkippedVersion_IsNotAnnouncedAutomatically_ButAManualCheckStillReportsIt()
    {
        var (service, checker, catalog) = Create();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(SkippedVersion: "v" + Newer) };
        checker.Latest = Release(Newer);

        var automatic = await service.CheckAsync(manual: false);
        Assert.Equal(UpdateCheckOutcome.Skipped, automatic.Outcome);
        Assert.Null(service.Available);

        var manual = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckOutcome.Available, manual.Outcome);
        Assert.NotNull(service.Available);
    }

    [Fact]
    public async Task AnEvenNewerVersion_IsAnnounced_AfterSkippingTheOlderOne()
    {
        var (service, checker, catalog) = Create();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(SkippedVersion: Newer) };
        checker.Latest = Release(NextVersion(2));

        Assert.Equal(UpdateCheckOutcome.Available, (await service.CheckAsync(manual: false)).Outcome);
    }

    [Fact]
    public async Task Skip_SavesTheVersion_AndClearsTheNotice_WithoutTouchingTheOtherSettings()
    {
        var (service, checker, catalog) = Create();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(WorkbenchTopmost: true, SpriteTopmost: false) };
        checker.Latest = Release(Newer);
        await service.CheckAsync(manual: false);

        await service.SkipAsync(service.Available!);

        Assert.Null(service.Available);
        var saved = Assert.Single(catalog.BehaviorSaves);
        Assert.Equal(Newer, saved.SkippedVersion);
        Assert.True(saved.WorkbenchTopmost);
        Assert.False(saved.SpriteTopmost);
    }

    [Fact]
    public async Task Dismiss_ClearsTheNotice_WithoutRemembering()
    {
        var (service, checker, catalog) = Create();
        checker.Latest = Release(Newer);
        await service.CheckAsync(manual: false);

        service.Dismiss();

        Assert.Null(service.Available);
        Assert.Empty(catalog.BehaviorSaves);
    }

    [Fact]
    public async Task AFailedCheck_ReportsTheReason_AndKeepsQuiet()
    {
        var (service, checker, _) = Create();
        checker.Failure = new UpdateCheckException("連不上更新伺服器。");

        var result = await service.CheckAsync(manual: false);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal("連不上更新伺服器。", result.Message);
        Assert.Null(service.Available);
    }

    [Fact]
    public async Task ACheckThatTimesOut_IsAFailure_NotAnException()
    {
        var (service, checker, _) = Create();
        checker.Failure = new TaskCanceledException();   // 內部逾時（不是呼叫端取消）

        Assert.Equal(UpdateCheckOutcome.Failed, (await service.CheckAsync(manual: true)).Outcome);
    }

    [Fact]
    public async Task CancellingTheCall_StillCancels()
    {
        var (service, checker, _) = Create();
        checker.Failure = new OperationCanceledException();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckAsync(manual: true, cts.Token));
    }

    [Fact]
    public async Task ANoLongerNewerResult_ClearsAnEarlierNotice()
    {
        var (service, checker, _) = Create();
        checker.Latest = Release(Newer);
        await service.CheckAsync(manual: false);
        checker.Latest = Release(AppVersion.Current);

        await service.CheckAsync(manual: true);

        Assert.Null(service.Available);
    }
}
