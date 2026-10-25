using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Web;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定視窗「網路」（M17 D1、D8）：Proxy 模式與驗證、不經過 Proxy 的位址、自動使用 Windows 登入的網站、網路工具的逾時。
/// 和語音頁一樣編輯的是草稿，按「儲存」才寫進設定；Proxy 密碼只存在 <see cref="ISecretStore"/>，這裡只知道有沒有存著。
/// 測試連線用**編輯中**的設定（還沒儲存也可以），密碼用輸入的；沒有輸入時由測試器讀已存的。
/// </summary>
public partial class NetworkViewModel : ObservableObject
{
    private const string DefaultTestUrl = "https://example.com/";

    private readonly IModelCatalog _catalog;
    private readonly INetworkTester _tester;
    private NetworkSettings _saved;

    public NetworkViewModel(IModelCatalog catalog, INetworkTester tester, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _tester = tester;
        _saved = catalog.Current.Network;
        ApplyDraft(_saved);
        StoredPasswordLoaded = RefreshStoredPasswordAsync();
        catalog.Changed += (_, settings) => dispatcher.Post(() => OnCatalogChanged(settings.Network));
    }

    /// <summary>建構時開始讀取「Proxy 密碼有沒有存著」的工作；測試用它等待初始狀態。</summary>
    public Task StoredPasswordLoaded { get; }

    /// <summary>0＝跟隨系統設定、1＝使用 PAC 網址、2＝手動指定、3＝不使用（對應 <see cref="ProxyMode"/>）。</summary>
    [ObservableProperty]
    private int _modeIndex;

    [ObservableProperty]
    private string _pacUrl = "";

    [ObservableProperty]
    private string _manualProxy = "";

    /// <summary>不經過 Proxy 的位址，一行一個。</summary>
    [ObservableProperty]
    private string _bypassText = "";

    /// <summary>0＝不需要、1＝使用目前登入的帳號、2＝帳號與密碼（對應 <see cref="ProxyAuthMode"/>）。</summary>
    [ObservableProperty]
    private int _authIndex;

    [ObservableProperty]
    private string _proxyUser = "";

    /// <summary>輸入中的新密碼（遮罩）；空白表示不變。</summary>
    [ObservableProperty]
    private string _newPassword = "";

    [ObservableProperty]
    private bool _revealPassword;

    /// <summary>已存密碼時按了「更換」：顯示輸入框。</summary>
    [ObservableProperty]
    private bool _replacingPassword;

    [ObservableProperty]
    private bool _hasStoredPassword;

    /// <summary>自動使用 Windows 登入身分的網站，一行一個（D8）。</summary>
    [ObservableProperty]
    private string _integratedSitesText = "";

    /// <summary>文字輸入的逾時秒數；驗證 5～120。</summary>
    [ObservableProperty]
    private string _timeoutText = "";

    /// <summary>測試網址：不存進設定、不算草稿的變更。</summary>
    [ObservableProperty]
    private string _testUrl = DefaultTestUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult), nameof(ShowTestOk), nameof(ShowTestFailure))]
    private string? _testResultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTestOk), nameof(ShowTestFailure))]
    private bool _testSucceeded;

    // ---- 顯示條件 ----

    private ProxyMode Mode => (ProxyMode)ModeIndex;

    private ProxyAuthMode Auth => (ProxyAuthMode)AuthIndex;

    public bool ShowPac => ModeIndex == (int)ProxyMode.Pac;

    public bool ShowManual => ModeIndex == (int)ProxyMode.Manual;

    /// <summary>不使用 Proxy 時不需要驗證，整段隱藏。</summary>
    public bool ShowAuth => ModeIndex != (int)ProxyMode.None;

    public bool ShowAccount => ShowAuth && AuthIndex == (int)ProxyAuthMode.Account;

    public bool ShowPasswordStatus => HasStoredPassword && !ReplacingPassword;

    public bool ShowPasswordInput => !HasStoredPassword || ReplacingPassword;

    // ---- 驗證（只檢查目前顯示中的欄位；原因文字來自 NetworkRules）----

    public string? PacUrlError => ShowPac ? NetworkRules.ValidatePacUrl(PacUrl) : null;

    public string? ManualProxyError => ShowManual ? NetworkRules.ValidateManualProxy(ManualProxy) : null;

    public string? BypassError => ShowManual ? NetworkRules.ValidateHostPatterns(NetworkRules.ParseLines(BypassText)) : null;

    public string? ProxyUserError => ShowAccount ? NetworkRules.ValidateProxyUser(ProxyUser) : null;

    /// <summary>帳號與密碼：還沒有存的密碼，或按了更換，就一定要輸入。</summary>
    public string? PasswordError =>
        ShowAccount && ShowPasswordInput && string.IsNullOrWhiteSpace(NewPassword) ? Strings.Network_PasswordRequired : null;

    public string? SitesError => NetworkRules.ValidateHostPatterns(NetworkRules.ParseLines(IntegratedSitesText));

    public string? TimeoutError => NetworkRules.ValidateTimeout(TimeoutText);

    public string? TestUrlError => NetworkRules.ValidateTestUrl(TestUrl);

    /// <summary>儲存的第一個錯誤（順序同畫面由上到下）；測試網址不算。</summary>
    public string? FirstError =>
        PacUrlError ?? ManualProxyError ?? BypassError ?? ProxyUserError ?? PasswordError ?? SitesError ?? TimeoutError;

    /// <summary>草稿與已儲存的設定不同（或輸入了新密碼）。</summary>
    public bool IsDirty => !SameSettings(BuildDraft(), _saved) || !string.IsNullOrWhiteSpace(NewPassword);

    public bool CanSave => IsDirty && FirstError is null && !IsSaving;

    /// <summary>儲存停用時，旁邊那一行 Caption 寫的原因；沒有要儲存的變更時不說明。</summary>
    public string? SaveBlockedReason => IsDirty ? FirstError : null;

    public bool HasSaveBlockedReason => SaveBlockedReason is not null;

    /// <summary>測試連線停用的原因：設定或測試網址有錯時，就是那個錯誤。</summary>
    public string? TestBlockedReason => FirstError ?? TestUrlError;

    public bool HasTestBlockedReason => TestBlockedReason is not null;

    public bool CanTest => FirstError is null && TestUrlError is null && !IsTesting;

    public bool CanRevert => IsDirty && !IsSaving;

    public bool HasMessage => Message is not null;

    public bool HasTestResult => TestResultText is not null;

    public bool ShowTestOk => HasTestResult && TestSucceeded;

    public bool ShowTestFailure => HasTestResult && !TestSucceeded;

    public bool ShowTestingText => IsTesting;

    public bool ShowSavingText => IsSaving;

    // ---- 草稿 ----

    /// <summary>依目前的欄位組出設定（逾時解析失敗時用已儲存的值，最後正規化）。</summary>
    public NetworkSettings BuildDraft() => new NetworkSettings
    {
        Mode = Mode,
        PacUrl = PacUrl.Trim(),
        ManualProxy = ManualProxy.Trim(),
        BypassList = NetworkRules.ParseLines(BypassText),
        Auth = Auth,
        ProxyUser = ProxyUser.Trim(),
        IntegratedAuthSites = NetworkRules.ParseLines(IntegratedSitesText),
        TimeoutSeconds = int.TryParse(TimeoutText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : _saved.TimeoutSeconds,
    }.Normalize();

    // ---- 草稿的變動：清掉舊的測試結果，重新算狀態 ----

    partial void OnModeIndexChanged(int value) => EditChanged();

    partial void OnPacUrlChanged(string value) => EditChanged();

    partial void OnManualProxyChanged(string value) => EditChanged();

    partial void OnBypassTextChanged(string value) => EditChanged();

    partial void OnAuthIndexChanged(int value) => EditChanged();

    partial void OnProxyUserChanged(string value) => EditChanged();

    partial void OnNewPasswordChanged(string value) => EditChanged();

    partial void OnIntegratedSitesTextChanged(string value) => EditChanged();

    partial void OnTimeoutTextChanged(string value) => EditChanged();

    partial void OnTestUrlChanged(string value) => EditChanged();

    partial void OnReplacingPasswordChanged(bool value) => RefreshDerived();

    partial void OnHasStoredPasswordChanged(bool value) => RefreshDerived();

    partial void OnIsSavingChanged(bool value) => RefreshDerived();

    partial void OnIsTestingChanged(bool value) => RefreshDerived();

    partial void OnTestResultTextChanged(string? value) => RefreshDerived();

    private void EditChanged()
    {
        TestResultText = null;
        RefreshDerived();
    }

    private void RefreshDerived()
    {
        OnPropertyChanged(nameof(ShowPac));
        OnPropertyChanged(nameof(ShowManual));
        OnPropertyChanged(nameof(ShowAuth));
        OnPropertyChanged(nameof(ShowAccount));
        OnPropertyChanged(nameof(ShowPasswordStatus));
        OnPropertyChanged(nameof(ShowPasswordInput));
        OnPropertyChanged(nameof(PacUrlError));
        OnPropertyChanged(nameof(ManualProxyError));
        OnPropertyChanged(nameof(BypassError));
        OnPropertyChanged(nameof(ProxyUserError));
        OnPropertyChanged(nameof(PasswordError));
        OnPropertyChanged(nameof(SitesError));
        OnPropertyChanged(nameof(TimeoutError));
        OnPropertyChanged(nameof(TestUrlError));
        OnPropertyChanged(nameof(FirstError));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(HasSaveBlockedReason));
        OnPropertyChanged(nameof(TestBlockedReason));
        OnPropertyChanged(nameof(HasTestBlockedReason));
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(ShowTestingText));
        OnPropertyChanged(nameof(ShowSavingText));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    // ---- 儲存與還原 ----

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var draft = BuildDraft();
        var password = ShowAccount && !string.IsNullOrWhiteSpace(NewPassword) ? new SecretValue(NewPassword) : null;
        IsSaving = true;
        try
        {
            await _catalog.SetNetworkAsync(draft, password);
            _saved = _catalog.Current.Network;
            NewPassword = "";
            ReplacingPassword = false;
            TestResultText = null;
            await RefreshStoredPasswordAsync();
            ApplyDraft(_saved);
            SetMessage(Strings.Network_Saved, isError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or SecretStoreException)
        {
            // 我們自己丟的（欄位驗證）訊息可直接顯示；其餘的一律用固定文字，不含密碼
            SetMessage(ex is ArgumentException ? ex.Message : Strings.Settings_SaveFailed, isError: true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>把草稿改回已儲存的值，並取消輸入中的密碼（還沒儲存）。</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        NewPassword = "";
        ReplacingPassword = false;
        ApplyDraft(_saved);
        Message = null;
    }

    // ---- Proxy 密碼 ----

    [RelayCommand]
    private void ReplacePassword() => ReplacingPassword = true;

    [RelayCommand]
    private void CancelReplacePassword()
    {
        NewPassword = "";
        ReplacingPassword = false;
    }

    // ---- 測試連線 ----

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        var draft = BuildDraft();
        var password = ShowAccount && !string.IsNullOrWhiteSpace(NewPassword) ? new SecretValue(NewPassword) : null;
        IsTesting = true;
        TestResultText = null;
        try
        {
            var result = await _tester.TestAsync(draft, password, new Uri(TestUrl.Trim()), CancellationToken.None);
            TestSucceeded = result.Success;
            TestResultText = result.Message;
        }
        catch (SecretStoreException)
        {
            TestSucceeded = false;
            TestResultText = Strings.Network_TestFailed;
        }
        finally
        {
            IsTesting = false;
        }
    }

    // ---- 內部 ----

    private async Task RefreshStoredPasswordAsync() =>
        HasStoredPassword = await _catalog.HasProxyPasswordAsync();

    private void OnCatalogChanged(NetworkSettings network)
    {
        if (SameSettings(network, _saved))
            return;
        var wasDirty = IsDirty;
        _saved = network;
        if (!wasDirty)
            ApplyDraft(network);   // 沒有改到一半：跟著目前的設定；有未儲存的變更就不覆蓋
    }

    private void ApplyDraft(NetworkSettings settings)
    {
        ModeIndex = (int)settings.Mode;
        PacUrl = settings.PacUrl;
        ManualProxy = settings.ManualProxy;
        BypassText = string.Join(Environment.NewLine, settings.BypassList);
        AuthIndex = (int)settings.Auth;
        ProxyUser = settings.ProxyUser;
        IntegratedSitesText = string.Join(Environment.NewLine, settings.IntegratedAuthSites);
        TimeoutText = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        RefreshDerived();
    }

    /// <summary>比較兩份設定的內容（清單要逐項比，record 預設的相等比的是參考）。</summary>
    private static bool SameSettings(NetworkSettings a, NetworkSettings b) =>
        a.Mode == b.Mode
        && a.PacUrl == b.PacUrl
        && a.ManualProxy == b.ManualProxy
        && a.BypassList.SequenceEqual(b.BypassList)
        && a.Auth == b.Auth
        && a.ProxyUser == b.ProxyUser
        && a.IntegratedAuthSites.SequenceEqual(b.IntegratedAuthSites)
        && a.TimeoutSeconds == b.TimeoutSeconds;

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}
