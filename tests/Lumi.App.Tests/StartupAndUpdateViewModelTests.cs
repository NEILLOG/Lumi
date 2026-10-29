using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Updates;

namespace Lumi.App.Tests;

/// <summary>「啟動與更新」設定區（開機自動啟動、啟動時檢查、立即檢查）與工作台頂端的新版提醒。</summary>
public sealed class StartupAndUpdateViewModelTests
{
    // ---- 開機後自動啟動 ----

    [Fact]
    public void TheCheckbox_ReflectsWhatTheSystemSays()
    {
        using var host = new SettingsHost();
        var enabled = new StartupViewModel(host.Catalog, new SyncUiDispatcher(), new FakeLaunchAtLogin(enabled: true));
        var disabled = new StartupViewModel(host.Catalog, new SyncUiDispatcher(), new FakeLaunchAtLogin(enabled: false));

        Assert.True(enabled.LaunchAtLogin);
        Assert.False(disabled.LaunchAtLogin);
        Assert.True(enabled.LaunchSupported);
    }

    [Fact]
    public void TurningItOn_AsksTheSystem_AndTurningItOffUndoesIt()
    {
        using var host = new SettingsHost();

        host.General.Startup.LaunchAtLogin = true;
        Assert.True(host.LaunchAtLogin.Enabled);
        Assert.Null(host.General.Startup.Error);

        host.General.Startup.LaunchAtLogin = false;
        Assert.False(host.LaunchAtLogin.Enabled);
    }

    [Fact]
    public void WhenTheSystemRefuses_TheCheckboxGoesBack_AndTheReasonIsShown()
    {
        using var host = new SettingsHost();
        host.LaunchAtLogin.Failure = new UnauthorizedAccessException();

        host.General.Startup.LaunchAtLogin = true;

        Assert.False(host.General.Startup.LaunchAtLogin);
        Assert.Equal(Strings.Startup_LaunchFailed, host.General.Startup.Error);
        Assert.True(host.General.Startup.HasError);
    }

    [Fact]
    public void WhenNotSupported_TheSwitchCannotBeTurnedOn()
    {
        using var host = new SettingsHost();
        var launch = new FakeLaunchAtLogin(supported: false);
        var vm = new StartupViewModel(host.Catalog, new SyncUiDispatcher(), launch);

        vm.LaunchAtLogin = true;

        Assert.False(vm.LaunchSupported);
        Assert.True(vm.LaunchUnsupported);
        Assert.False(vm.LaunchAtLogin);
        Assert.False(launch.Enabled);
    }

    [Fact]
    public void WithoutALauncherService_TheSwitchIsUnsupported()
    {
        using var host = new SettingsHost();

        Assert.False(new StartupViewModel(host.Catalog, new SyncUiDispatcher()).LaunchSupported);
    }

    // ---- 啟動時檢查 ----

    [Fact]
    public async Task CheckForUpdates_IsOnByDefault_AndTheChoiceIsSaved()
    {
        using var host = new SettingsHost();
        Assert.True(host.General.Startup.CheckForUpdates);

        host.General.Startup.CheckForUpdates = false;
        await SettingsHost.WaitUntil(() => !host.Catalog.Current.Behavior.CheckForUpdates);

        Assert.False(host.Catalog.Current.Behavior.CheckForUpdates);
    }

    [Fact]
    public async Task SavingTheCheckbox_KeepsTheOtherBehaviorSettings()
    {
        using var host = new SettingsHost();
        await host.Catalog.SetBehaviorAsync(host.Catalog.Current.Behavior with { WorkbenchTopmost = true, SkippedVersion = "2.0.0" });

        host.General.Startup.CheckForUpdates = false;
        await SettingsHost.WaitUntil(() => !host.Catalog.Current.Behavior.CheckForUpdates);

        var behavior = host.Catalog.Current.Behavior;
        Assert.True(behavior.WorkbenchTopmost);
        Assert.Equal("2.0.0", behavior.SkippedVersion);
    }

    [Fact]
    public async Task ReadOnlyCatalog_RevertsTheCheckbox_AndShowsTheError()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);

        host.General.Startup.CheckForUpdates = false;
        await SettingsHost.WaitUntil(() => host.General.Startup.Error is not null);

        Assert.True(host.General.Startup.CheckForUpdates);
        Assert.NotNull(host.General.Startup.Error);
    }

    // ---- 立即檢查 ----

    [Fact]
    public void TheVersion_IsShown() =>
        Assert.Contains(AppVersion.Current, new StartupViewModel(new FakeModelCatalog(), new SyncUiDispatcher()).VersionText);

    [Fact]
    public async Task CheckNow_ReportsThatYouAreUpToDate()
    {
        using var host = new SettingsHost();

        await host.General.Startup.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal([true], host.Updates.Checks);   // 手動檢查
        Assert.Equal(Strings.Update_UpToDate, host.General.Startup.Status);
        Assert.False(host.General.Startup.HasUpdate);
        Assert.False(host.General.Startup.IsChecking);
    }

    [Fact]
    public async Task CheckNow_ReportsANewVersion_AndOffersTheDownloadPage()
    {
        using var host = new SettingsHost();
        var release = FakeUpdateService.Release("9.9.9");
        host.Updates.NextResult = new UpdateCheckResult(UpdateCheckOutcome.Available, release);

        await host.General.Startup.CheckNowCommand.ExecuteAsync(null);
        await host.General.Startup.OpenDownloadCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.Update_AvailableFormat, "9.9.9"), host.General.Startup.Status);
        Assert.True(host.General.Startup.HasUpdate);
        Assert.Equal([release.PageUrl], host.UrlLauncher.Opened);
    }

    [Fact]
    public async Task CheckNow_ShowsWhyItFailed()
    {
        using var host = new SettingsHost();
        host.Updates.NextResult = new UpdateCheckResult(UpdateCheckOutcome.Failed, Message: "找不到伺服器。");

        await host.General.Startup.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.Update_FailedFormat, "找不到伺服器。"), host.General.Startup.Status);
        Assert.False(host.General.Startup.HasUpdate);
    }

    [Fact]
    public void WithoutAnUpdateService_CheckNowIsUnavailable()
    {
        var vm = new StartupViewModel(new FakeModelCatalog(), new SyncUiDispatcher());

        Assert.False(vm.CanCheck);
        Assert.False(vm.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public async Task ANewVersionFoundAtStartup_IsAlreadyShownWhenSettingsOpen()
    {
        using var host = new SettingsHost();
        host.Updates.Announce(FakeUpdateService.Release("9.9.9"));
        var vm = new StartupViewModel(host.Catalog, new SyncUiDispatcher(), host.LaunchAtLogin, host.Updates, host.UrlLauncher);

        Assert.True(vm.HasUpdate);
        Assert.Equal(string.Format(Strings.Update_AvailableFormat, "9.9.9"), vm.Status);
        await vm.OpenDownloadCommand.ExecuteAsync(null);
        Assert.Single(host.UrlLauncher.Opened);
    }

    // ---- 工作台頂端的新版提醒 ----

    private static UpdateNoticeViewModel Notice(FakeUpdateService updates, FakeUrlLauncher? launcher = null) =>
        new(updates, launcher ?? new FakeUrlLauncher(), new SyncUiDispatcher());

    [Fact]
    public void NoNotice_WhenThereIsNoNewVersion()
    {
        var notice = Notice(new FakeUpdateService());

        Assert.False(notice.IsVisible);
    }

    [Fact]
    public void TheNotice_AppearsWhenANewVersionIsFound_AndGoesWhenItIsGone()
    {
        var updates = new FakeUpdateService();
        var notice = Notice(updates);

        updates.Announce(FakeUpdateService.Release("9.9.9"));
        Assert.True(notice.IsVisible);
        Assert.Equal(string.Format(Strings.Update_BannerFormat, "9.9.9"), notice.Title);
        Assert.Contains(AppVersion.Current, notice.Subtitle);

        updates.Announce(null);
        Assert.False(notice.IsVisible);
    }

    [Fact]
    public void ANoticeThatWasAlreadyPending_ShowsRightAway() =>
        Assert.True(Notice(NewUpdates("9.9.9")).IsVisible);

    private static FakeUpdateService NewUpdates(string version, string notes = "## 新功能\n- 好用")
    {
        var updates = new FakeUpdateService();
        updates.Announce(FakeUpdateService.Release(version, notes));
        return updates;
    }

    [Fact]
    public void TheNotes_AreCollapsedUntilAsked_AndEmptyNotesSaySo()
    {
        var notice = Notice(NewUpdates("9.9.9"));
        Assert.False(notice.NotesExpanded);
        Assert.Equal(Strings.Update_ShowNotes, notice.NotesToggleText);
        Assert.Contains("好用", notice.Notes);

        notice.ToggleNotesCommand.Execute(null);
        Assert.True(notice.NotesExpanded);
        Assert.Equal(Strings.Update_HideNotes, notice.NotesToggleText);

        Assert.Equal(Strings.Update_NoNotes, Notice(NewUpdates("9.9.9", notes: "")).Notes);
    }

    [Fact]
    public async Task Download_OpensTheReleasePage()
    {
        var launcher = new FakeUrlLauncher();
        var notice = Notice(NewUpdates("9.9.9"), launcher);

        await notice.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["https://github.com/NEILLOG/DesktopAgent/releases/tag/v9.9.9"], launcher.Opened.Select(u => u.AbsoluteUri));
        Assert.False(notice.HasMessage);
    }

    [Fact]
    public async Task WhenTheBrowserCannotOpen_TheAddressIsShownSoItCanBeCopied()
    {
        var notice = Notice(NewUpdates("9.9.9"), new FakeUrlLauncher(succeeds: false));

        await notice.DownloadCommand.ExecuteAsync(null);

        Assert.True(notice.HasMessage);
        Assert.Contains("https://github.com/NEILLOG/DesktopAgent/releases/tag/v9.9.9", notice.Message);
    }

    [Fact]
    public async Task Skip_RemembersTheVersion_AndHidesTheNotice()
    {
        var updates = NewUpdates("9.9.9");
        var notice = Notice(updates);

        await notice.SkipCommand.ExecuteAsync(null);

        Assert.Equal(["9.9.9"], updates.Skipped.Select(u => u.Version));
        Assert.False(notice.IsVisible);
    }

    [Fact]
    public async Task WhenSkippingCannotBeSaved_TheNoticeStays_WithAnExplanation()
    {
        var updates = NewUpdates("9.9.9");
        updates.SkipFailure = new IOException();
        var notice = Notice(updates);

        await notice.SkipCommand.ExecuteAsync(null);

        Assert.True(notice.IsVisible);
        Assert.Equal(Strings.Settings_SaveFailed, notice.Message);
    }

    [Fact]
    public void Dismiss_OnlyHidesIt_NothingIsRemembered()
    {
        var updates = NewUpdates("9.9.9");
        var notice = Notice(updates);

        notice.DismissCommand.Execute(null);

        Assert.False(notice.IsVisible);
        Assert.Equal(1, updates.Dismissals);
        Assert.Empty(updates.Skipped);
    }
}
