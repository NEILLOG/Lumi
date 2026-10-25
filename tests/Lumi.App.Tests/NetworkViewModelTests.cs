using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Web;

namespace Lumi.App.Tests;

/// <summary>M17 17G：設定視窗「網路」——草稿與驗證、模式與驗證方式的顯示、Proxy 密碼、儲存、測試連線。</summary>
public sealed class NetworkViewModelTests
{
    /// <summary>建立 ViewModel 並等待「密碼有沒有存著」的初始讀取。</summary>
    private static async Task<(NetworkViewModel Vm, NetworkCatalogSpy Catalog, FakeNetworkTester Tester)> CreateAsync(
        NetworkSettings? saved = null, bool hasPassword = false)
    {
        var catalog = new NetworkCatalogSpy
        {
            Current = NetworkCatalogSpy.Default with { Network = (saved ?? NetworkSettings.Default).Normalize() },
            HasProxyPassword = hasPassword,
        };
        var tester = new FakeNetworkTester();
        var vm = new NetworkViewModel(catalog, tester, new SyncUiDispatcher());
        await vm.StoredPasswordLoaded;
        return (vm, catalog, tester);
    }

    // ---- 初始狀態與顯示條件 ----

    [Fact]
    public async Task LoadsEveryFieldFromTheSavedSettings()
    {
        var saved = new NetworkSettings
        {
            Mode = ProxyMode.Manual,
            ManualProxy = "proxy.example.com:8080",
            BypassList = ["*.Example.com", "10.*"],
            Auth = ProxyAuthMode.Account,
            ProxyUser = "CORP\\amy",
            IntegratedAuthSites = ["*.corp.example.com"],
            TimeoutSeconds = 45,
        };

        var (vm, _, _) = await CreateAsync(saved, hasPassword: true);

        Assert.Equal((int)ProxyMode.Manual, vm.ModeIndex);
        Assert.Equal("proxy.example.com:8080", vm.ManualProxy);
        Assert.Equal(new[] { "*.example.com", "10.*" }, vm.BypassText.Split(Environment.NewLine));
        Assert.Equal((int)ProxyAuthMode.Account, vm.AuthIndex);
        Assert.Equal("CORP\\amy", vm.ProxyUser);
        Assert.Equal("*.corp.example.com", vm.IntegratedSitesText);
        Assert.Equal("45", vm.TimeoutText);
        Assert.Equal("https://example.com/", vm.TestUrl);
        Assert.True(vm.HasStoredPassword);
        Assert.False(vm.IsDirty);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task ModeAndAuthIndexesShowTheMatchingFields()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.False(vm.ShowPac);
        Assert.False(vm.ShowManual);
        Assert.True(vm.ShowAuth);
        Assert.False(vm.ShowAccount);

        vm.ModeIndex = (int)ProxyMode.Pac;
        Assert.True(vm.ShowPac);
        Assert.False(vm.ShowManual);

        vm.ModeIndex = (int)ProxyMode.Manual;
        Assert.False(vm.ShowPac);
        Assert.True(vm.ShowManual);

        vm.ModeIndex = (int)ProxyMode.None;
        Assert.False(vm.ShowManual);
        Assert.False(vm.ShowAuth);            // 不使用 Proxy：驗證整段隱藏
        Assert.False(vm.ShowAccount);

        vm.ModeIndex = (int)ProxyMode.System;
        vm.AuthIndex = (int)ProxyAuthMode.Account;
        Assert.True(vm.ShowAccount);
        vm.AuthIndex = (int)ProxyAuthMode.Integrated;
        Assert.False(vm.ShowAccount);
    }

    [Fact]
    public async Task OnlyTheDisplayedFieldsAreValidated()
    {
        var (vm, _, _) = await CreateAsync();

        vm.ManualProxy = "not a proxy";             // 模式是跟隨系統：手動位址不檢查
        vm.BypassText = "bad host!";
        vm.TimeoutText = "30";

        Assert.Null(vm.ManualProxyError);
        Assert.Null(vm.BypassError);
        Assert.Null(vm.FirstError);
        vm.TimeoutText = "31";                        // 只有這個欄位改了，仍然可以儲存
        Assert.True(vm.CanSave);
    }

    // ---- 各欄位的錯誤（文字來自 NetworkRules）----

    [Fact]
    public async Task PacUrlErrorIsOnlyShownForPac()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Null(vm.PacUrlError);
        vm.ModeIndex = (int)ProxyMode.Pac;
        Assert.Equal(NetworkRules.ValidatePacUrl(""), vm.PacUrlError);
        Assert.NotNull(vm.PacUrlError);

        vm.PacUrl = "ftp://wpad.example.com/proxy.pac";
        Assert.NotNull(vm.PacUrlError);
        Assert.Equal(vm.PacUrlError, vm.FirstError);

        vm.PacUrl = "http://wpad.example.com/proxy.pac";
        Assert.Null(vm.PacUrlError);
        Assert.Null(vm.FirstError);
    }

    [Fact]
    public async Task ManualProxyErrorRejectsBadAddresses()
    {
        var (vm, _, _) = await CreateAsync();
        vm.ModeIndex = (int)ProxyMode.Manual;

        vm.ManualProxy = "";
        Assert.Equal(NetworkRules.ValidateManualProxy(""), vm.ManualProxyError);
        vm.ManualProxy = "proxy";                      // 沒有 port
        Assert.NotNull(vm.ManualProxyError);
        vm.ManualProxy = "http://user:pw@proxy:8080";  // 不可以放帳號密碼
        Assert.NotNull(vm.ManualProxyError);
        vm.ManualProxy = "proxy.example.com:8080";
        Assert.Null(vm.ManualProxyError);
    }

    [Fact]
    public async Task BypassErrorChecksEachLine()
    {
        var (vm, _, _) = await CreateAsync();
        vm.ModeIndex = (int)ProxyMode.Manual;

        vm.BypassText = "*.example.com\n10.*\n\n";
        Assert.Null(vm.BypassError);

        vm.BypassText = "*.example.com\nbad host!";
        Assert.Equal(NetworkRules.ValidateHostPattern("bad host!"), vm.BypassError);
    }

    [Fact]
    public async Task ProxyUserErrorIsOnlyShownForAccountAuth()
    {
        var (vm, _, _) = await CreateAsync();

        Assert.Null(vm.ProxyUserError);
        vm.AuthIndex = (int)ProxyAuthMode.Account;
        Assert.Equal(NetworkRules.ValidateProxyUser(""), vm.ProxyUserError);
        vm.ProxyUser = "CORP\\amy";
        Assert.Null(vm.ProxyUserError);

        vm.ModeIndex = (int)ProxyMode.None;            // 不使用 Proxy：帳號隱藏，不檢查
        Assert.Null(vm.ProxyUserError);
    }

    [Fact]
    public async Task PasswordIsRequiredWhenNoneIsStoredOrWhenReplacing()
    {
        var (vm, _, _) = await CreateAsync();
        vm.AuthIndex = (int)ProxyAuthMode.Account;

        Assert.Equal(Strings.Network_PasswordRequired, vm.PasswordError);
        vm.NewPassword = "secret";
        Assert.Null(vm.PasswordError);

        var (stored, _, _) = await CreateAsync(new NetworkSettings { Auth = ProxyAuthMode.Account, ProxyUser = "CORP\\amy" }, hasPassword: true);
        Assert.Null(stored.PasswordError);             // 已存密碼、沒有更換：不需要輸入
        stored.ReplacePasswordCommand.Execute(null);
        Assert.Equal(Strings.Network_PasswordRequired, stored.PasswordError);
        stored.CancelReplacePasswordCommand.Execute(null);
        Assert.Null(stored.PasswordError);
    }

    [Fact]
    public async Task SitesErrorChecksEachLine()
    {
        var (vm, _, _) = await CreateAsync();

        vm.IntegratedSitesText = "*.corp.example.com";
        Assert.Null(vm.SitesError);
        vm.IntegratedSitesText = "*.corp.example.com\nnot valid!";
        Assert.NotNull(vm.SitesError);
    }

    [Theory]
    [InlineData("4", false)]
    [InlineData("5", true)]
    [InlineData("120", true)]
    [InlineData("121", false)]
    [InlineData("abc", false)]
    public async Task TimeoutMustBeFrom5To120(string text, bool valid)
    {
        var (vm, _, _) = await CreateAsync();

        vm.TimeoutText = text;

        Assert.Equal(valid, vm.TimeoutError is null);
        Assert.Equal(NetworkRules.ValidateTimeout(text), vm.TimeoutError);
    }

    [Theory]
    [InlineData("ftp://example.com/", false)]
    [InlineData("example.com", false)]
    [InlineData("https://example.com/", true)]
    public async Task TestUrlErrorBlocksTheTestButIsNotASaveError(string url, bool valid)
    {
        var (vm, _, _) = await CreateAsync();

        vm.TestUrl = url;

        Assert.Equal(valid, vm.TestUrlError is null);
        Assert.Equal(valid, vm.CanTest);
        Assert.Null(vm.FirstError);                    // 測試網址不算儲存的錯誤
    }

    // ---- 草稿、儲存與還原 ----

    [Fact]
    public async Task IsDirtyAndCanSaveFollowTheDraft()
    {
        var (vm, _, _) = await CreateAsync();
        Assert.False(vm.IsDirty);
        Assert.False(vm.CanSave);

        vm.TimeoutText = "60";
        Assert.True(vm.IsDirty);
        Assert.True(vm.CanSave);
        Assert.True(vm.CanRevert);

        vm.TimeoutText = "3";
        Assert.False(vm.CanSave);
        Assert.Equal(NetworkRules.ValidateTimeout("3"), vm.SaveBlockedReason);
    }

    [Fact]
    public async Task TestUrlDoesNotMakeTheDraftDirty()
    {
        var (vm, _, _) = await CreateAsync();

        vm.TestUrl = "https://intranet.example.com/";

        Assert.False(vm.IsDirty);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task SaveSendsTheNormalizedDraftAndNoPasswordWhenNoneTyped()
    {
        var (vm, catalog, _) = await CreateAsync();
        vm.ModeIndex = (int)ProxyMode.Manual;
        vm.ManualProxy = " proxy.example.com:8080 ";
        vm.BypassText = "10.*\n*.Example.com\n\n";
        vm.AuthIndex = (int)ProxyAuthMode.Integrated;
        vm.IntegratedSitesText = "*.Corp.example.com";
        vm.TimeoutText = "60";

        await vm.SaveCommand.ExecuteAsync(null);

        var save = Assert.Single(catalog.NetworkSaves);
        Assert.Equal(ProxyMode.Manual, save.Settings.Mode);
        Assert.Equal("proxy.example.com:8080", save.Settings.ManualProxy);
        Assert.Equal(new[] { "*.example.com", "10.*" }, save.Settings.BypassList);
        Assert.Equal(ProxyAuthMode.Integrated, save.Settings.Auth);
        Assert.Equal(new[] { "*.corp.example.com" }, save.Settings.IntegratedAuthSites);
        Assert.Equal(60, save.Settings.TimeoutSeconds);
        Assert.Null(save.Password);
        Assert.False(vm.IsDirty);
        Assert.Equal(Strings.Network_Saved, vm.Message);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public async Task SaveSendsTypedPasswordOnlyForAccountAuth()
    {
        var (vm, catalog, _) = await CreateAsync();
        vm.AuthIndex = (int)ProxyAuthMode.Account;
        vm.ProxyUser = "CORP\\amy";
        vm.NewPassword = "pw-1234";

        await vm.SaveCommand.ExecuteAsync(null);

        var save = Assert.Single(catalog.NetworkSaves);
        Assert.Equal("pw-1234", save.Password?.Reveal());
        Assert.Equal("", vm.NewPassword);
        Assert.True(vm.HasStoredPassword);
    }

    [Fact]
    public async Task SwitchingToIntegratedDoesNotSendThePassword()
    {
        var (vm, catalog, _) = await CreateAsync();
        vm.AuthIndex = (int)ProxyAuthMode.Integrated;
        vm.NewPassword = "typed-but-unused";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(catalog.NetworkSaves).Password);
    }

    [Fact]
    public async Task SavingWithAStoredPasswordKeepsIt()
    {
        var (vm, catalog, _) = await CreateAsync(
            new NetworkSettings { Auth = ProxyAuthMode.Account, ProxyUser = "CORP\\amy" }, hasPassword: true);
        vm.TimeoutText = "50";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(Assert.Single(catalog.NetworkSaves).Password);
        Assert.True(vm.HasStoredPassword);
        Assert.True(vm.ShowPasswordStatus);
    }

    [Fact]
    public async Task ArgumentExceptionFromTheCatalogIsShownAsAnError()
    {
        var (vm, catalog, _) = await CreateAsync();
        catalog.SaveFailure = new ArgumentException("Proxy 位址有誤");
        vm.TimeoutText = "50";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Proxy 位址有誤", vm.Message);
        Assert.True(vm.MessageIsError);
        Assert.True(vm.IsDirty);                       // 沒有儲存成功：草稿保留
    }

    [Fact]
    public async Task IoFailureShowsTheGenericSaveError()
    {
        var (vm, catalog, _) = await CreateAsync();
        catalog.SaveFailure = new IOException("磁碟已滿");
        vm.TimeoutText = "50";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Settings_SaveFailed, vm.Message);
        Assert.True(vm.MessageIsError);
    }

    [Fact]
    public async Task RevertPutsTheSavedValuesBack()
    {
        var (vm, _, _) = await CreateAsync();
        vm.ModeIndex = (int)ProxyMode.Pac;
        vm.PacUrl = "http://wpad.example.com/proxy.pac";
        vm.NewPassword = "typed";

        Assert.True(vm.CanRevert);
        vm.RevertCommand.Execute(null);

        Assert.Equal((int)ProxyMode.System, vm.ModeIndex);
        Assert.Equal("", vm.PacUrl);
        Assert.Equal("", vm.NewPassword);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task InvalidTimeoutTextFallsBackToTheSavedValueInTheDraft()
    {
        var (vm, _, _) = await CreateAsync(new NetworkSettings { TimeoutSeconds = 45 });

        vm.TimeoutText = "abc";

        Assert.Equal(45, vm.BuildDraft().TimeoutSeconds);
        Assert.NotNull(vm.TimeoutError);
        Assert.False(vm.CanSave);
    }

    [Fact]
    public async Task SitesAreNormalizedInTheDraft()
    {
        var (vm, _, _) = await CreateAsync();

        vm.IntegratedSitesText = "*.B.com\n*.a.com\n*.B.com";

        Assert.Equal(new[] { "*.a.com", "*.b.com" }, vm.BuildDraft().IntegratedAuthSites);
    }

    // ---- Proxy 密碼的顯示 ----

    [Fact]
    public async Task StoredPasswordShowsTheStatusUntilReplaced()
    {
        var (vm, _, _) = await CreateAsync(new NetworkSettings { Auth = ProxyAuthMode.Account, ProxyUser = "CORP\\amy" }, hasPassword: true);

        Assert.True(vm.ShowPasswordStatus);
        Assert.False(vm.ShowPasswordInput);

        vm.ReplacePasswordCommand.Execute(null);
        Assert.False(vm.ShowPasswordStatus);
        Assert.True(vm.ShowPasswordInput);
    }

    // ---- 測試連線 ----

    [Fact]
    public async Task TestUsesTheUnsavedDraftAndShowsTheResult()
    {
        var (vm, catalog, tester) = await CreateAsync();
        tester.Result = new NetworkTestResult(true, "連線成功（經 Proxy）");
        vm.ModeIndex = (int)ProxyMode.Pac;
        vm.PacUrl = "http://wpad.example.com/proxy.pac";
        vm.TestUrl = "https://intranet.example.com/";

        await vm.TestCommand.ExecuteAsync(null);

        var call = Assert.Single(tester.Calls);
        Assert.Equal(ProxyMode.Pac, call.Draft.Mode);
        Assert.Equal("http://wpad.example.com/proxy.pac", call.Draft.PacUrl);
        Assert.Null(call.Password);
        Assert.Equal(new Uri("https://intranet.example.com/"), call.Target);
        Assert.True(vm.ShowTestOk);
        Assert.False(vm.ShowTestFailure);
        Assert.Equal("連線成功（經 Proxy）", vm.TestResultText);
        Assert.Empty(catalog.NetworkSaves);            // 測試不會儲存草稿
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task TestShowsAFailure()
    {
        var (vm, _, tester) = await CreateAsync();
        tester.Result = new NetworkTestResult(false, "找不到 Proxy 主機");

        await vm.TestCommand.ExecuteAsync(null);

        Assert.True(vm.ShowTestFailure);
        Assert.False(vm.ShowTestOk);
        Assert.Equal("找不到 Proxy 主機", vm.TestResultText);
    }

    [Fact]
    public async Task TestPassesTheTypedPasswordForAccountAuth()
    {
        var (vm, _, tester) = await CreateAsync();
        vm.AuthIndex = (int)ProxyAuthMode.Account;
        vm.ProxyUser = "CORP\\amy";
        vm.NewPassword = "pw-typed";

        await vm.TestCommand.ExecuteAsync(null);

        Assert.Equal("pw-typed", tester.Calls.Single().Password);
    }

    [Fact]
    public async Task ChangingAFieldClearsTheTestResult()
    {
        var (vm, _, _) = await CreateAsync();
        await vm.TestCommand.ExecuteAsync(null);
        Assert.True(vm.HasTestResult);

        vm.PacUrl = "http://wpad.example.com/proxy.pac";

        Assert.False(vm.HasTestResult);
        Assert.False(vm.ShowTestOk);
    }

    [Fact]
    public async Task TestIsBlockedWhileTheDraftHasAnError()
    {
        var (vm, _, _) = await CreateAsync();
        vm.ModeIndex = (int)ProxyMode.Manual;
        vm.ManualProxy = "";

        Assert.False(vm.CanTest);
        Assert.Equal(NetworkRules.ValidateManualProxy(""), vm.TestBlockedReason);
        Assert.False(vm.TestCommand.CanExecute(null));
    }

    // ---- 目錄變動 ----

    [Fact]
    public async Task CatalogChangeReloadsTheDraftWhenThereAreNoEdits()
    {
        var (vm, catalog, _) = await CreateAsync();

        catalog.Current = catalog.Current with
        {
            Network = new NetworkSettings { Mode = ProxyMode.Manual, ManualProxy = "proxy.example.com:8080" }.Normalize(),
        };
        catalog.Raise();

        Assert.Equal((int)ProxyMode.Manual, vm.ModeIndex);
        Assert.Equal("proxy.example.com:8080", vm.ManualProxy);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task CatalogChangeDoesNotOverwriteUnsavedEdits()
    {
        var (vm, catalog, _) = await CreateAsync();
        vm.TimeoutText = "50";

        catalog.Current = catalog.Current with
        {
            Network = new NetworkSettings { Mode = ProxyMode.None }.Normalize(),
        };
        catalog.Raise();

        Assert.Equal("50", vm.TimeoutText);
        Assert.Equal((int)ProxyMode.System, vm.ModeIndex);
        Assert.True(vm.IsDirty);
    }

    /// <summary>
    /// 記錄每次 <see cref="SetNetworkAsync"/> 的參數（設定與密碼）。其餘成員這個測試用不到；
    /// 密碼只在這個假目錄裡記錄，正式實作由 Infrastructure 負責。
    /// </summary>
    private sealed class NetworkCatalogSpy : IModelCatalog
    {
        public static readonly AppSettings Default = new([FakeModelCatalog.Connection], [FakeModelCatalog.Model],
            FakeModelCatalog.Model.Id, FakeModelCatalog.Model.Id, ThemePreference.System);

        public AppSettings Current { get; set; } = Default;

        public string? LoadProblem => null;

        public event EventHandler<AppSettings>? Changed;

        public void Raise() => Changed?.Invoke(this, Current);

        public bool HasProxyPassword { get; set; }

        /// <summary>不為 null 時，<see cref="SetNetworkAsync"/> 丟出這個例外（不寫入）。</summary>
        public Exception? SaveFailure { get; set; }

        public List<(NetworkSettings Settings, SecretValue? Password)> NetworkSaves { get; } = [];

        public Task SetNetworkAsync(NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default)
        {
            if (SaveFailure is { } failure)
                throw failure;
            NetworkSaves.Add((settings, newProxyPassword));
            var normalized = settings.Normalize();
            if (newProxyPassword is not null)
                HasProxyPassword = true;
            if (normalized.Auth != ProxyAuthMode.Account)
                HasProxyPassword = false;
            Current = Current with { Network = normalized };
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => Task.FromResult(HasProxyPassword);

        public Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveModelAsync(ModelSettings model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetThemeIdAsync(string themeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetNotesFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveMcpServerAsync(McpServerSettings server, McpServerSecrets? newSecrets, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public ResolvedModel? Resolve(string? modelSettingsId) => throw new NotSupportedException();
    }
}
