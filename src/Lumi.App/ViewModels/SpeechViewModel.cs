using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定視窗「語音」：服務端點、語言、逾時、Token。和人格頁一樣編輯的是草稿，按「儲存」才寫進設定；
/// Token 只存在 <see cref="ISecretStore"/>，Host 綁定規則見 <see cref="IModelCatalog.SetSpeechAsync"/>。
/// 測試連線用**編輯中**的端點、語言、逾時，Token 用輸入的；沒有輸入時用已儲存的（且端點主機要與綁定的相同）。
/// </summary>
public partial class SpeechViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly ISpeechConnectionTester _tester;
    private readonly ISecretStore _secrets;
    private readonly IConfirmService _confirm;
    private SpeechSettings _saved;

    public SpeechViewModel(IModelCatalog catalog, ISpeechConnectionTester tester, ISecretStore secrets, IConfirmService confirm,
        IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _tester = tester;
        _secrets = secrets;
        _confirm = confirm;
        _saved = catalog.Current.Speech;
        ApplyDraft(_saved);
        StoredTokenLoaded = RefreshStoredTokenAsync();
        catalog.Changed += (_, settings) => dispatcher.Post(() => OnCatalogChanged(settings.Speech));
    }

    /// <summary>建構時開始讀取「Token 有沒有存著」的工作；測試用它等待初始狀態。</summary>
    public Task StoredTokenLoaded { get; }

    [ObservableProperty]
    private string _endpoint = "";

    [ObservableProperty]
    private string _language = "";

    /// <summary>文字輸入的逾時秒數；驗證 5～600，不合法時儲存與測試停用並說明原因。</summary>
    [ObservableProperty]
    private string _timeoutText = "";

    /// <summary>輸入中的新 Token（遮罩）；空白表示不變。</summary>
    [ObservableProperty]
    private string _newToken = "";

    [ObservableProperty]
    private bool _revealToken;

    /// <summary>已存 Token 時按了「更換」：顯示輸入框。</summary>
    [ObservableProperty]
    private bool _replacingToken;

    [ObservableProperty]
    private bool _hasStoredToken;

    /// <summary>已存 Token 的末 4 碼（只顯示用，從不讀回完整內容）。</summary>
    [ObservableProperty]
    private string? _storedTokenHint;

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

    // ---- 驗證與狀態（唯讀，由草稿與已存的值算出）----

    /// <summary>端點的原因；空白時是「請先輸入服務端點」，其他情況用 <see cref="SpeechEndpointRules"/> 的繁中原因。</summary>
    public string? EndpointError
    {
        get
        {
            if (Endpoint.Trim().Length == 0)
                return Strings.Speech_EndpointRequired;
            var check = SpeechEndpointRules.Validate(Endpoint);
            return check.IsValid ? null : check.Reason;
        }
    }

    public string? TimeoutError => ParsedTimeout is null ? Strings.Speech_TimeoutInvalid : null;

    /// <summary>草稿端點的主機（端點無效時是 null）。</summary>
    private string? DraftHost
    {
        get
        {
            var check = SpeechEndpointRules.Validate(Endpoint);
            return check.IsValid ? check.Uri!.Host : null;
        }
    }

    /// <summary>已存 Token 綁定的主機與草稿端點的主機不同（或端點被清空）：舊 Token 不能送到新主機，要求重新輸入。</summary>
    public bool HostChanged => HasStoredToken && !string.Equals(DraftHost, _saved.TokenHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>顯示 Token 輸入框：沒有 Token、按了更換，或主機變了。</summary>
    public bool ShowTokenInput => !HasStoredToken || ReplacingToken || HostChanged;

    /// <summary>顯示「已設定 · 結尾 xxxx」與更換／清除（主機變了時改顯示警告）。</summary>
    public bool ShowTokenStatus => HasStoredToken && !HostChanged;

    public string TokenStatusText => !HasStoredToken
        ? Strings.Speech_TokenNotSet
        : StoredTokenHint is { } hint
            ? string.Format(CultureInfo.CurrentCulture, Strings.Speech_TokenSetHint, hint)
            : Strings.Speech_TokenSet;

    /// <summary>草稿與已存的設定不同（或輸入了新 Token）。</summary>
    public bool IsDirty
    {
        get
        {
            var draft = DraftSettings();
            return draft.Endpoint != _saved.Endpoint || draft.Language != _saved.Language
                || draft.TimeoutSeconds != _saved.TimeoutSeconds || TypedToken.Length > 0;
        }
    }

    public bool CanSave => IsDirty && EndpointError is null && TimeoutError is null && !IsSaving;

    /// <summary>儲存停用時，旁邊那一行 Caption 寫的原因；沒有要儲存的變更時不說明。</summary>
    public string? SaveBlockedReason => IsDirty ? (EndpointError ?? TimeoutError) : null;

    public bool HasSaveBlockedReason => SaveBlockedReason is not null;

    /// <summary>測試連線停用的原因；沒有原因就能按。</summary>
    public string? TestBlockedReason =>
        EndpointError ?? TimeoutError
        ?? (TypedToken.Length == 0 && !HasStoredToken ? Strings.Speech_TestNeedsToken : null)
        ?? (TypedToken.Length == 0 && HostChanged ? Strings.Speech_TokenHostChanged : null);

    public bool HasTestBlockedReason => TestBlockedReason is not null;

    public bool CanTest => TestBlockedReason is null && !IsTesting;

    public bool CanRevert => IsDirty && !IsSaving;

    public bool HasMessage => Message is not null;

    public bool HasTestResult => TestResultText is not null;

    public bool ShowTestOk => HasTestResult && TestSucceeded;

    public bool ShowTestFailure => HasTestResult && !TestSucceeded;

    public bool ShowTestingText => IsTesting;

    private string TypedToken => NewToken.Trim();

    private int? ParsedTimeout =>
        int.TryParse(TimeoutText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds is >= SpeechSettings.MinTimeoutSeconds and <= SpeechSettings.MaxTimeoutSeconds
            ? seconds
            : null;

    private SpeechSettings DraftSettings() =>
        new(Endpoint.Trim(), Language.Trim(), ParsedTimeout ?? _saved.TimeoutSeconds);

    // ---- 草稿的變動：清掉舊的測試結果，重新算狀態 ----

    partial void OnEndpointChanged(string value) => EditChanged();

    partial void OnLanguageChanged(string value) => EditChanged();

    partial void OnTimeoutTextChanged(string value) => EditChanged();

    partial void OnNewTokenChanged(string value) => EditChanged();

    partial void OnReplacingTokenChanged(bool value) => RefreshDerived();

    partial void OnHasStoredTokenChanged(bool value) => RefreshDerived();

    partial void OnStoredTokenHintChanged(string? value) => RefreshDerived();

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
        OnPropertyChanged(nameof(EndpointError));
        OnPropertyChanged(nameof(TimeoutError));
        OnPropertyChanged(nameof(HostChanged));
        OnPropertyChanged(nameof(ShowTokenInput));
        OnPropertyChanged(nameof(ShowTokenStatus));
        OnPropertyChanged(nameof(TokenStatusText));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(HasSaveBlockedReason));
        OnPropertyChanged(nameof(TestBlockedReason));
        OnPropertyChanged(nameof(HasTestBlockedReason));
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(ShowTestingText));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        ClearTokenCommand.NotifyCanExecuteChanged();
    }

    // ---- 儲存與還原 ----

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var draft = DraftSettings();
        var token = TypedToken.Length > 0 ? new SecretValue(TypedToken) : null;
        IsSaving = true;
        try
        {
            await _catalog.SetSpeechAsync(draft, token);
            _saved = _catalog.Current.Speech;
            NewToken = "";
            ReplacingToken = false;
            TestResultText = null;
            await RefreshStoredTokenAsync();
            ApplyDraft(_saved);
            SetMessage(Strings.Speech_Saved, isError: false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or SecretStoreException)
        {
            // 我們自己丟的（端點、逾時、設定檔版本太新）訊息不含 Token；其餘的一律用固定文字
            SetMessage(ex is ArgumentException or InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed, isError: true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>把草稿改回已儲存的值，並取消輸入中的 Token（還沒儲存）。</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        NewToken = "";
        ReplacingToken = false;
        ApplyDraft(_saved);
        Message = null;
    }

    [RelayCommand]
    private void SetLanguage(string? code)
    {
        Language = code ?? "";
        Message = null;
    }

    // ---- Token ----

    [RelayCommand]
    private void ReplaceToken() => ReplacingToken = true;

    [RelayCommand]
    private void CancelReplaceToken()
    {
        NewToken = "";
        ReplacingToken = false;
    }

    [RelayCommand(CanExecute = nameof(HasStoredToken))]
    private async Task ClearTokenAsync()
    {
        if (!await _confirm.ConfirmDestructiveAsync(Strings.Speech_TokenClearConfirm, Strings.Confirm_Clear))
            return;

        try
        {
            await _catalog.ClearSpeechTokenAsync();
            NewToken = "";
            ReplacingToken = false;
            TestResultText = null;
            await RefreshStoredTokenAsync();
            SetMessage(Strings.Speech_Saved, isError: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretStoreException or InvalidOperationException)
        {
            SetMessage(Strings.Settings_SaveFailed, isError: true);
        }
    }

    // ---- 測試連線 ----

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        var settings = DraftSettings();
        IsTesting = true;
        TestResultText = null;
        try
        {
            var token = await ResolveTestTokenAsync();
            await _tester.TestAsync(settings, token, CancellationToken.None);
            TestSucceeded = true;
            TestResultText = Strings.Speech_TestOk;
        }
        catch (SpeechFailure ex)
        {
            TestSucceeded = false;
            TestResultText = ex.Message;
        }
        catch (SecretStoreException)
        {
            TestSucceeded = false;
            TestResultText = Strings.Speech_TokenUnreadable;
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>輸入的 Token 優先；沒有輸入時讀已存的（只在這裡讀，讀出來直接交給測試器，不留在 ViewModel）。</summary>
    private async Task<SecretValue> ResolveTestTokenAsync()
    {
        if (TypedToken.Length > 0)
            return new SecretValue(TypedToken);
        var stored = await _secrets.GetAsync(SpeechSecrets.WhisperToken);
        return stored ?? throw new SpeechFailure(SpeechFailureKind.NotConfigured, Strings.Speech_TestNeedsToken, false);
    }

    // ---- 內部 ----

    private async Task RefreshStoredTokenAsync()
    {
        HasStoredToken = await _catalog.HasSpeechTokenAsync();
        StoredTokenHint = HasStoredToken ? _saved.TokenHint : null;
    }

    private void OnCatalogChanged(SpeechSettings speech)
    {
        if (speech == _saved)
            return;
        var wasDirty = IsDirty;
        _saved = speech;
        if (!wasDirty)
            ApplyDraft(speech);   // 沒有改到一半：跟著目前的設定
        _ = RefreshStoredTokenAsync();
    }

    private void ApplyDraft(SpeechSettings settings)
    {
        Endpoint = settings.Endpoint;
        Language = settings.Language;
        TimeoutText = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        RefreshDerived();
    }

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}
