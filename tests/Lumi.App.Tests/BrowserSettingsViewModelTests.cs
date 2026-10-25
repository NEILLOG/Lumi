using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Browser;

namespace Lumi.App.Tests;

/// <summary>M18 18I：設定頁「網路」底部的「瀏覽器」區段——選擇與視窗（立即生效）、偵測結果、清除登入狀態。</summary>
public sealed class BrowserSettingsViewModelTests
{
    private static readonly InstalledBrowser Edge = new(BrowserKind.Edge, @"C:\Edge\msedge.exe", "138.0.3351.65");
    private static readonly InstalledBrowser Chrome = new(BrowserKind.Chrome, @"C:\Chrome\chrome.exe", "155.0.8059.85");

    private sealed class Rig
    {
        public Rig(BrowserSettings? saved = null, params InstalledBrowser[] installed)
        {
            Catalog = new FakeModelCatalog();
            if (saved is not null)
                Catalog.Current = Catalog.Current with { Browser = saved };
            Locator = new FakeBrowserLocator(installed);
            Sessions = new FakeBrowserSessions();
            Confirm = new FakeConfirmService();
            // 計數「寫入」：FakeModelCatalog.SetBrowserAsync 會通知 Changed，所以這裡數到的就是寫入次數
            Catalog.Changed += (_, _) => Saves++;
            Vm = new BrowserSettingsViewModel(Catalog, Locator, Sessions, Confirm, new SyncUiDispatcher());
        }

        public FakeModelCatalog Catalog { get; }
        public FakeBrowserLocator Locator { get; }
        public FakeBrowserSessions Sessions { get; }
        public FakeConfirmService Confirm { get; }
        public BrowserSettingsViewModel Vm { get; }
        public int Saves { get; set; }
    }

    // ---- 載入與立即生效 ----

    [Fact]
    public void LoadsTheSavedChoiceAndWindowSetting()
    {
        var rig = new Rig(new BrowserSettings { Choice = BrowserChoice.Edge, ShowWindow = true }, Edge);

        Assert.Equal((int)BrowserChoice.Edge, rig.Vm.ChoiceIndex);
        Assert.True(rig.Vm.ShowWindow);
        Assert.Equal(0, rig.Saves);   // 載入不寫入
    }

    [Fact]
    public void Defaults_AreAutomaticAndBackground()
    {
        var rig = new Rig(null, Edge);

        Assert.Equal(0, rig.Vm.ChoiceIndex);
        Assert.False(rig.Vm.ShowWindow);
    }

    [Fact]
    public async Task ChoosingABrowser_SavesAtOnce_WithoutAStoreButton()
    {
        var rig = new Rig(null, Edge, Chrome);

        rig.Vm.ChoiceIndex = (int)BrowserChoice.Chrome;

        // 只看 Current 是否立刻改變；存檔是 fire-and-forget，等它完成再確認
        await WaitForAsync(() => rig.Catalog.Current.Browser.Choice == BrowserChoice.Chrome);
        Assert.Equal(BrowserChoice.Chrome, rig.Catalog.Current.Browser.Choice);
        Assert.False(rig.Catalog.Current.Browser.ShowWindow);   // 其他欄位保留
        Assert.Equal(1, rig.Saves);
    }

    [Fact]
    public async Task CheckingShowWindow_SavesAtOnce()
    {
        var rig = new Rig(null, Edge);

        rig.Vm.ShowWindow = true;

        await WaitForAsync(() => rig.Catalog.Current.Browser.ShowWindow);
        Assert.True(rig.Catalog.Current.Browser.ShowWindow);
        Assert.Equal(BrowserChoice.Auto, rig.Catalog.Current.Browser.Choice);   // 其他欄位保留
    }

    [Fact]
    public void SettingTheSameValue_DoesNotSave()
    {
        var rig = new Rig(null, Edge);

        rig.Vm.ChoiceIndex = 0;
        rig.Vm.ShowWindow = false;

        Assert.Equal(0, rig.Saves);
    }

    [Fact]
    public void CatalogChanged_UpdatesTheScreen_WithoutSavingAgain()
    {
        var rig = new Rig(null, Edge, Chrome);

        rig.Catalog.Current = rig.Catalog.Current with { Browser = new BrowserSettings { Choice = BrowserChoice.Chrome, ShowWindow = true } };
        var before = rig.Saves;
        rig.Catalog.Raise();   // 這一次通知是測試自己觸發的；畫面更新不能再寫入（寫入會多一次通知）

        Assert.Equal((int)BrowserChoice.Chrome, rig.Vm.ChoiceIndex);
        Assert.True(rig.Vm.ShowWindow);
        Assert.Equal(before + 1, rig.Saves);
    }

    // ---- 偵測結果 ----

    [Fact]
    public void DetectionText_ListsBothBrowsersWithVersions()
    {
        var rig = new Rig(null, Edge, Chrome);

        Assert.Equal("找到：Microsoft Edge 138.0.3351.65、Google Chrome 155.0.8059.85", rig.Vm.DetectionText);
        Assert.False(rig.Vm.HasProblem);
    }

    [Fact]
    public void DetectionText_WithOnlyEdge_ListsEdgeOnly()
    {
        var rig = new Rig(null, Edge);

        Assert.Equal("找到：Microsoft Edge 138.0.3351.65", rig.Vm.DetectionText);
        Assert.False(rig.Vm.HasProblem);
    }

    [Fact]
    public void DetectionText_WithoutAVersion_ShowsOnlyTheName()
    {
        var rig = new Rig(null, new InstalledBrowser(BrowserKind.Chrome, @"C:\Chrome\chrome.exe", null));

        Assert.Equal("找到：Google Chrome", rig.Vm.DetectionText);
    }

    [Fact]
    public void DetectionText_WhenNothingIsInstalled_IsTheNotFoundWarning()
    {
        var rig = new Rig(null);

        Assert.Equal(Strings.Browser_NotFound, rig.Vm.DetectionText);
        Assert.True(rig.Vm.HasProblem);
    }

    [Fact]
    public void ChoosingABrowserThatIsNotInstalled_ShowsWhatIsFoundAndWhy()
    {
        var rig = new Rig(null, Edge);

        rig.Vm.ChoiceIndex = (int)BrowserChoice.Chrome;

        Assert.True(rig.Vm.HasProblem);
        Assert.Equal("找到：Microsoft Edge 138.0.3351.65\n找不到所選的瀏覽器。", rig.Vm.DetectionText);
    }

    [Fact]
    public void RefreshCommand_DetectsAgain()
    {
        var rig = new Rig(null);
        Assert.True(rig.Vm.HasProblem);

        rig.Locator.Installed.Add(Edge);
        rig.Vm.RefreshCommand.Execute(null);

        Assert.False(rig.Vm.HasProblem);
        Assert.Equal("找到：Microsoft Edge 138.0.3351.65", rig.Vm.DetectionText);
    }

    [Fact]
    public void DetectionRunsAgainOnlyWhenTheChoiceChanges()
    {
        var rig = new Rig(null, Edge, Chrome);
        var calls = rig.Locator.ResolveCalls.Count;

        rig.Catalog.Current = rig.Catalog.Current with { Browser = new BrowserSettings { ShowWindow = true } };
        rig.Catalog.Raise();

        Assert.Equal(calls, rig.Locator.ResolveCalls.Count);   // 只有視窗變了，不重新偵測
    }

    // ---- 清除登入狀態 ----

    [Fact]
    public async Task Clear_WhenTheUserCancelsTheConfirmation_DoesNothing()
    {
        var rig = new Rig(null, Edge);
        rig.Confirm.Answer = false;

        await rig.Vm.ClearProfileCommand.ExecuteAsync(null);

        Assert.Equal(0, rig.Sessions.ClearCalls);
        Assert.Null(rig.Vm.Message);
        Assert.Equal(Strings.Browser_ClearConfirm, rig.Confirm.Asked.Single().Message);
        Assert.Equal(Strings.Browser_ClearConfirmButton, rig.Confirm.Asked.Single().ConfirmText);
    }

    [Fact]
    public async Task Clear_AfterConfirming_ClearsTheProfileAndShowsAMessage()
    {
        var rig = new Rig(null, Edge);

        await rig.Vm.ClearProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Sessions.ClearCalls);
        Assert.Equal(Strings.Browser_Cleared, rig.Vm.Message);
        Assert.False(rig.Vm.MessageIsError);
        Assert.True(rig.Vm.HasMessage);
    }

    [Fact]
    public async Task Clear_WhenTheSessionFails_ShowsTheReasonAsAnError()
    {
        var rig = new Rig(null, Edge);
        rig.Sessions.ClearFailure = new BrowserException(BrowserFailureKind.Other, "瀏覽器正在使用中，請先關閉它。");

        await rig.Vm.ClearProfileCommand.ExecuteAsync(null);

        Assert.Equal("瀏覽器正在使用中，請先關閉它。", rig.Vm.Message);
        Assert.True(rig.Vm.MessageIsError);
        Assert.False(rig.Vm.IsClearing);
        Assert.True(rig.Vm.ClearProfileCommand.CanExecute(null));
    }

    [Fact]
    public async Task Clear_IsDisabledWhileRunning()
    {
        var rig = new Rig(null, Edge);
        rig.Sessions.ClearGate = new TaskCompletionSource();

        var running = rig.Vm.ClearProfileCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsClearing);
        Assert.False(rig.Vm.ClearProfileCommand.CanExecute(null));

        rig.Sessions.ClearGate.SetResult();
        await running;

        Assert.False(rig.Vm.IsClearing);
        Assert.True(rig.Vm.ClearProfileCommand.CanExecute(null));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
    }
}
