using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.App.Tests;

/// <summary>M6 6B：設定視窗「語音」——端點驗證、逾時、草稿與儲存、Token（含主機綁定）、測試連線。</summary>
public sealed class SpeechViewModelTests
{
    private const string Endpoint = "https://speech.example.com/v1";

    private static async Task<SpeechViewModel> StoredAsync(SettingsHost host, string token = "wh-token-5678")
    {
        await host.Catalog.SetSpeechAsync(new SpeechSettings(Endpoint, "zh", 120), new SecretValue(token));
        await host.Speech.StoredTokenLoaded;
        return host.Speech;
    }

    // ---- 初始狀態與驗證 ----

    [Fact]
    public void StartsWithTheDefaults_AndAnEmptyEndpointBlocksSaveAndTest()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;

        Assert.Equal("", vm.Endpoint);
        Assert.Equal("zh", vm.Language);
        Assert.Equal("120", vm.TimeoutText);
        Assert.False(vm.HasStoredToken);
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(Strings.Speech_EndpointRequired, vm.EndpointError);
        Assert.Null(vm.SaveBlockedReason);                       // 沒有要儲存的變更時不說明原因
        Assert.Equal(Strings.Speech_EndpointRequired, vm.TestBlockedReason);
        Assert.False(vm.TestCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("http://speech.example.com")]
    [InlineData("ftp://speech.example.com")]
    [InlineData("https://speech.example.com/v1?token=1")]
    [InlineData("https://speech.example.com/v1#x")]
    [InlineData("https://user:pw@speech.example.com")]
    [InlineData("不是網址")]
    public void EndpointErrors_AreTheCoreRulesReason(string endpoint)
    {
        using var host = new SettingsHost();
        var vm = host.Speech;

        vm.Endpoint = endpoint;

        Assert.Equal(SpeechEndpointRules.Validate(endpoint).Reason, vm.EndpointError);
        Assert.NotNull(vm.EndpointError);
    }

    [Theory]
    [InlineData("https://speech.example.com/v1")]
    [InlineData("http://localhost:8080")]
    public void ValidEndpoints_HaveNoError(string endpoint)
    {
        using var host = new SettingsHost();
        host.Speech.Endpoint = endpoint;
        Assert.Null(host.Speech.EndpointError);
    }

    [Theory]
    [InlineData("5", true)]
    [InlineData(" 600 ", true)]
    [InlineData("4", false)]
    [InlineData("601", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData("1.5", false)]
    [InlineData("-10", false)]
    public void TimeoutMustBeAWholeNumberFrom5To600(string text, bool valid)
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;

        vm.TimeoutText = text;

        Assert.Equal(valid, vm.TimeoutError is null);
        if (!valid)
        {
            Assert.Equal(Strings.Speech_TimeoutInvalid, vm.TimeoutError);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.Equal(Strings.Speech_TimeoutInvalid, vm.SaveBlockedReason);
        }
    }

    [Fact]
    public void AnInvalidEndpoint_DisablesSave_AndSaysWhy()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;

        vm.Endpoint = "http://speech.example.com";

        Assert.True(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(vm.EndpointError, vm.SaveBlockedReason);
    }

    // ---- 草稿與儲存 ----

    [Fact]
    public async Task Save_PassesTheDraft_WithNoToken_WhenTheTokenFieldIsEmpty()
    {
        var catalog = new FakeModelCatalog();
        var vm = new SpeechViewModel(catalog, new FakeSpeechConnectionTester(), new InMemorySecretStore(), new FakeConfirmService(), new SyncUiDispatcher());
        vm.Endpoint = "  " + Endpoint + "  ";
        vm.Language = "en";
        vm.TimeoutText = "90";
        Assert.True(vm.IsDirty);

        await vm.SaveCommand.ExecuteAsync(null);

        var (settings, token) = Assert.Single(catalog.SpeechSaves);
        Assert.Equal(new SpeechSettings(Endpoint, "en", 90), settings);
        Assert.Null(token);                                       // Token 空白＝不變：傳 null，由 catalog 保留舊的
        Assert.False(vm.IsDirty);
        Assert.Equal(Strings.Speech_Saved, vm.Message);
        Assert.False(vm.MessageIsError);
    }

    [Fact]
    public async Task Save_PassesTheTypedToken_AsASecret()
    {
        var catalog = new FakeModelCatalog();
        var vm = new SpeechViewModel(catalog, new FakeSpeechConnectionTester(), new InMemorySecretStore(), new FakeConfirmService(), new SyncUiDispatcher());
        vm.Endpoint = Endpoint;
        vm.NewToken = "  wh-token-9999  ";

        await vm.SaveCommand.ExecuteAsync(null);

        var (_, token) = Assert.Single(catalog.SpeechSaves);
        Assert.Equal("wh-token-9999", token!.Reveal());
        Assert.Equal("", vm.NewToken);                            // 存了就清空輸入框
    }

    [Fact]
    public async Task Save_WithTheRealCatalog_StoresTheToken_AndShowsTheLastFour()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;
        vm.NewToken = "wh-token-5678";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasStoredToken);
        Assert.Equal("5678", vm.StoredTokenHint);
        Assert.Equal(string.Format(Strings.Speech_TokenSetHint, "5678"), vm.TokenStatusText);
        Assert.True(vm.ShowTokenStatus);
        Assert.False(vm.ShowTokenInput);
        Assert.Equal("5678", host.Catalog.Current.Speech.TokenHint);
        Assert.Equal("speech.example.com", host.Catalog.Current.Speech.TokenHost);
        Assert.True(host.Secrets.Items.ContainsKey(SpeechSecrets.WhisperToken.Name));
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Save_WithoutANewToken_KeepsTheStoredToken()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);

        vm.TimeoutText = "200";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.HasStoredToken);
        Assert.Equal(200, host.Catalog.Current.Speech.TimeoutSeconds);
        Assert.Equal("wh-token-5678", host.Secrets.Items[SpeechSecrets.WhisperToken.Name]);
    }

    [Fact]
    public async Task Revert_DiscardsTheDraft_AndTheTypedToken()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;
        vm.Language = "ja";
        vm.NewToken = "typed";
        Assert.True(vm.IsDirty);

        vm.RevertCommand.Execute(null);

        Assert.Equal("", vm.Endpoint);
        Assert.Equal("zh", vm.Language);
        Assert.Equal("", vm.NewToken);
        Assert.False(vm.IsDirty);
        Assert.Equal(Strings.Speech_EndpointRequired, vm.TestBlockedReason);
    }

    [Fact]
    public void SetLanguage_FillsTheQuickPick_AndEmptyMeansTheServiceDecides()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;

        vm.SetLanguageCommand.Execute("en");
        Assert.Equal("en", vm.Language);

        vm.SetLanguageCommand.Execute("");
        Assert.Equal("", vm.Language);
    }

    [Fact]
    public async Task AnExternalChange_FollowsTheSettings_WhenThereIsNoDraft()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;

        await host.Catalog.SetSpeechAsync(new SpeechSettings(Endpoint, "ja", 60), null);

        Assert.Equal(Endpoint, vm.Endpoint);
        Assert.Equal("ja", vm.Language);
        Assert.Equal("60", vm.TimeoutText);
        Assert.False(vm.IsDirty);
    }

    // ---- Token ----

    [Fact]
    public async Task ClearToken_AsksFirst_ThenRemovesTheSecret()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);

        host.Confirm.Answer = false;
        await vm.ClearTokenCommand.ExecuteAsync(null);
        Assert.True(vm.HasStoredToken);                          // 取消：不清除
        Assert.Single(host.Confirm.Asked);

        host.Confirm.Answer = true;
        await vm.ClearTokenCommand.ExecuteAsync(null);

        Assert.False(vm.HasStoredToken);
        Assert.False(host.Secrets.Items.ContainsKey(SpeechSecrets.WhisperToken.Name));
        Assert.Null(host.Catalog.Current.Speech.TokenHint);
        Assert.Null(host.Catalog.Current.Speech.TokenHost);
        Assert.Equal(Strings.Speech_TokenNotSet, vm.TokenStatusText);
        Assert.Equal(2, host.Confirm.Asked.Count);
    }

    [Fact]
    public async Task ReplaceAndCancel_TogglesTheInputAndForgetsTheTypedValue()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);

        vm.ReplaceTokenCommand.Execute(null);
        Assert.True(vm.ReplacingToken);
        Assert.True(vm.ShowTokenInput);

        vm.NewToken = "half-typed";
        vm.CancelReplaceTokenCommand.Execute(null);
        Assert.False(vm.ReplacingToken);
        Assert.Equal("", vm.NewToken);
        Assert.False(vm.ShowTokenInput);
    }

    [Fact]
    public async Task ChangingTheEndpointHost_RequiresANewToken_AndWarns()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);
        Assert.False(vm.HostChanged);

        vm.Endpoint = "https://other.example.com/v1";

        Assert.True(vm.HostChanged);
        Assert.True(vm.ShowTokenInput);
        Assert.False(vm.ShowTokenStatus);                         // 不再說「已設定」，改成警告
        Assert.Equal(Strings.Speech_TokenHostChanged, vm.TestBlockedReason);
        Assert.False(vm.CanTest);
    }

    [Fact]
    public async Task ASameHostWithDifferentCase_IsNotAChange()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);

        vm.Endpoint = "https://SPEECH.example.com/v2";

        Assert.False(vm.HostChanged);
        Assert.True(vm.ShowTokenStatus);
    }

    [Fact]
    public async Task SavingAChangedHost_WithoutAToken_DropsTheOldToken()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);
        vm.Endpoint = "https://other.example.com/v1";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.HasStoredToken);
        Assert.False(host.Secrets.Items.ContainsKey(SpeechSecrets.WhisperToken.Name));
        Assert.Null(host.Catalog.Current.Speech.TokenHost);
    }

    [Fact]
    public async Task ChangingTheHost_ThenTypingANewToken_BindsTheTokenToTheNewHost()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);
        vm.Endpoint = "https://other.example.com/v1";
        vm.NewToken = "wh-new-0001";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("other.example.com", host.Catalog.Current.Speech.TokenHost);
        Assert.Equal("0001", host.Catalog.Current.Speech.TokenHint);
        Assert.True(vm.HasStoredToken);
        Assert.False(vm.HostChanged);
    }

    // ---- 測試連線 ----

    [Fact]
    public async Task Test_UsesTheDraftValues_AndTheTypedToken()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;
        vm.Language = "en";
        vm.TimeoutText = "30";
        vm.NewToken = "typed-token";
        Assert.True(vm.CanTest);

        await vm.TestCommand.ExecuteAsync(null);

        var call = Assert.Single(host.SpeechTester.Calls);
        Assert.Equal(new SpeechSettings(Endpoint, "en", 30), call.Settings);
        Assert.Equal("typed-token", call.Token);
        Assert.True(vm.TestSucceeded);
        Assert.True(vm.ShowTestOk);
        Assert.False(vm.ShowTestFailure);
        Assert.Equal(Strings.Speech_TestOk, vm.TestResultText);
        Assert.False(vm.IsTesting);
        Assert.Equal("", host.Catalog.Current.Speech.Endpoint);   // 測試不會存任何東西
        Assert.False(vm.HasStoredToken);
    }

    [Fact]
    public async Task Test_WithoutATypedToken_UsesTheStoredOne()
    {
        using var host = new SettingsHost();
        var vm = await StoredAsync(host);
        vm.TimeoutText = "45";                                  // 編輯中、尚未儲存的逾時

        await vm.TestCommand.ExecuteAsync(null);

        var call = Assert.Single(host.SpeechTester.Calls);
        Assert.Equal("wh-token-5678", call.Token);
        Assert.Equal(45, call.Settings.TimeoutSeconds);
        Assert.True(vm.ShowTestOk);
    }

    [Fact]
    public async Task Test_WithoutAnyToken_IsBlocked_AndSaysWhy()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;

        Assert.False(vm.CanTest);
        Assert.Equal(Strings.Speech_TestNeedsToken, vm.TestBlockedReason);
        Assert.False(vm.TestCommand.CanExecute(null));
        Assert.Empty(host.SpeechTester.Calls);
    }

    [Fact]
    public async Task Test_Failure_ShowsTheFailureMessage()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;
        vm.NewToken = "bad";
        host.SpeechTester.Failure = new SpeechFailure(SpeechFailureKind.InvalidToken, "Token 無效，或沒有 WHISPER_API 權限。", false);

        await vm.TestCommand.ExecuteAsync(null);

        Assert.False(vm.TestSucceeded);
        Assert.True(vm.ShowTestFailure);
        Assert.False(vm.ShowTestOk);
        Assert.Equal("Token 無效，或沒有 WHISPER_API 權限。", vm.TestResultText);
    }

    [Fact]
    public async Task Test_WhileRunning_DisablesTheButton()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;
        vm.NewToken = "typed";
        var gate = new TaskCompletionSource();
        host.SpeechTester.Gate = gate;

        var running = vm.TestCommand.ExecuteAsync(null);

        Assert.True(vm.IsTesting);
        Assert.True(vm.ShowTestingText);
        Assert.False(vm.CanTest);
        Assert.False(vm.TestCommand.CanExecute(null));
        gate.SetResult();
        await running;
        Assert.False(vm.IsTesting);
        Assert.True(vm.CanTest);
    }

    [Fact]
    public async Task Test_WithAnUnreadableStoredToken_SaysSo()
    {
        using var host = new SettingsHost();
        await StoredAsync(host);
        var vm = new SpeechViewModel(host.Catalog, host.SpeechTester, new ThrowingSecretStore(), host.Confirm, new SyncUiDispatcher());
        await vm.StoredTokenLoaded;
        vm.Endpoint = Endpoint;

        await vm.TestCommand.ExecuteAsync(null);

        Assert.Empty(host.SpeechTester.Calls);
        Assert.Equal(Strings.Speech_TokenUnreadable, vm.TestResultText);
    }

    [Fact]
    public async Task EditingAfterATest_ClearsTheOldResult()
    {
        using var host = new SettingsHost();
        var vm = host.Speech;
        vm.Endpoint = Endpoint;
        vm.NewToken = "typed";
        await vm.TestCommand.ExecuteAsync(null);
        Assert.True(vm.ShowTestOk);

        vm.TimeoutText = "100";

        Assert.False(vm.HasTestResult);
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) =>
            throw new SecretStoreException("keychain locked");

        public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(true);
    }
}
