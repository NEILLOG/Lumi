using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>工作台標題列的圖釘（釘選在最上層）與小精靈設定頁的「永遠顯示在其他視窗上方」。</summary>
public sealed class TopmostTests
{
    private static (WorkbenchViewModel Vm, FakeModelCatalog Catalog) CreateWorkbench()
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        return (vm, catalog);
    }

    [Fact]
    public void TheWorkbench_IsAnOrdinaryWindowByDefault()
    {
        var (vm, _) = CreateWorkbench();

        Assert.False(vm.IsTopmost);
        Assert.Equal(Strings.Workbench_TopmostPin, vm.TopmostTip);
    }

    [Fact]
    public void ASavedPin_IsRestoredAtStartup()
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(WorkbenchTopmost: true) };
        var vm = new WorkbenchViewModel(new ConversationService(new SequenceChatProvider(), store, catalog), store, catalog,
            new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(),
            new SyncUiDispatcher(), new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance);

        Assert.True(vm.IsTopmost);
        Assert.Equal(Strings.Workbench_TopmostUnpin, vm.TopmostTip);
    }

    [Fact]
    public async Task ThePinButton_TogglesAndSaves_KeepingTheOtherSettings()
    {
        var (vm, catalog) = CreateWorkbench();
        catalog.Current = catalog.Current with { Behavior = new BehaviorSettings(SpriteTopmost: false, SkippedVersion: "2.0.0") };

        await vm.ToggleTopmostCommand.ExecuteAsync(null);

        Assert.True(vm.IsTopmost);
        var saved = Assert.Single(catalog.BehaviorSaves);
        Assert.True(saved.WorkbenchTopmost);
        Assert.False(saved.SpriteTopmost);
        Assert.Equal("2.0.0", saved.SkippedVersion);

        await vm.ToggleTopmostCommand.ExecuteAsync(null);
        Assert.False(vm.IsTopmost);
        Assert.False(catalog.Current.Behavior.WorkbenchTopmost);
    }

    [Fact]
    public async Task TheWindow_FollowsAChangeMadeElsewhere()
    {
        var (vm, catalog) = CreateWorkbench();

        await catalog.SetBehaviorAsync(new BehaviorSettings(WorkbenchTopmost: true));

        Assert.True(vm.IsTopmost);
    }

    [Fact]
    public async Task WhenTheSettingCannotBeSaved_ThePinGoesBack_AndTheUserIsTold()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);
        var store = new InMemoryConversationStore();
        var vm = new WorkbenchViewModel(new ConversationService(new SequenceChatProvider(), store, host.Catalog), store, host.Catalog,
            new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(),
            new SyncUiDispatcher(), new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance);

        await vm.ToggleTopmostCommand.ExecuteAsync(null);

        Assert.False(vm.IsTopmost);
        Assert.NotNull(vm.Notice);
    }

    // ---- 小精靈 ----

    [Fact]
    public void TheSprite_StaysOnTopByDefault()
    {
        using var host = new SettingsHost();

        Assert.True(host.Sprite.AlwaysOnTop);
    }

    [Fact]
    public async Task TheSpriteSwitch_IsSaved_WithoutTouchingTheOtherSettings()
    {
        using var host = new SettingsHost();
        await host.Catalog.SetBehaviorAsync(host.Catalog.Current.Behavior with { WorkbenchTopmost = true });

        host.Sprite.AlwaysOnTop = false;
        await SettingsHost.WaitUntil(() => !host.Catalog.Current.Behavior.SpriteTopmost);

        Assert.False(host.Catalog.Current.Behavior.SpriteTopmost);
        Assert.True(host.Catalog.Current.Behavior.WorkbenchTopmost);
    }

    [Fact]
    public async Task TheSpriteSwitch_FollowsAChangeMadeElsewhere()
    {
        using var host = new SettingsHost();

        await host.Catalog.SetBehaviorAsync(host.Catalog.Current.Behavior with { SpriteTopmost = false });

        Assert.False(host.Sprite.AlwaysOnTop);
    }

    [Fact]
    public async Task ReadOnlyCatalog_RevertsTheSpriteSwitch_AndShowsTheError()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);

        host.Sprite.AlwaysOnTop = false;
        await SettingsHost.WaitUntil(() => host.Sprite.Error is not null);

        Assert.True(host.Sprite.AlwaysOnTop);
    }
}
