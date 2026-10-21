using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.App.ViewModels;

/// <summary>設定視窗「連線」分頁：清單＋表單。已存的金鑰永遠不讀回來；只有輸入新金鑰才會寫入。</summary>
public partial class ConnectionsViewModel : ObservableObject
{
    public const string AnthropicTemplateUrl = "https://api.anthropic.com";
    public const string OpenAITemplateUrl = "https://api.openai.com/v1";

    private readonly IModelCatalog _catalog;
    private readonly IConnectionTester _tester;
    private readonly ConnectionStatusCache _status;
    private readonly IConfirmService _confirm;

    private string? _editingId;                                   // 正在編輯的連線 Id（新增時是預先產生的 Id）
    private (string Name, ProviderFormat Format, string BaseUrl) _snapshot = ("", ProviderFormat.OpenAICompatible, "");
    private CancellationTokenSource? _testCts;
    private ConnectionListItem? _selectedItem;
    private bool _loading;
    private bool _rebuildingItems;   // 重建清單時，清單控制項會把選取寫成 null：這時不能當成使用者取消選取

    public ConnectionsViewModel(IModelCatalog catalog, IConnectionTester tester, ConnectionStatusCache status,
        IConfirmService confirm, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _tester = tester;
        _status = status;
        _confirm = confirm;
        RefreshItems();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ListTitle));
        catalog.Changed += (_, _) => dispatcher.Post(RefreshItems);
        status.Changed += (_, _) => dispatcher.Post(() =>
        {
            RefreshItems();
            OnPropertyChanged(nameof(EditorStatus));
            OnPropertyChanged(nameof(EditorStatusText));
        });
    }

    public ObservableCollection<ConnectionListItem> Items { get; } = [];

    public IReadOnlyList<FormatOption> Formats => FormatOption.All;

    /// <summary>要求畫面把焦點放到金鑰輸入框（缺金鑰流程）。</summary>
    public event Action? FocusApiKeyRequested;

    public ConnectionListItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_rebuildingItems || ReferenceEquals(value, _selectedItem))
                return;
            // 有未儲存的變更要先詢問；詢問期間先讓畫面還原成原本的選取
            _ = SelectAsync(value);
            OnPropertyChanged();
        }
    }

    public bool HasEditor => _editingId is not null;

    public bool ShowPlaceholder => !HasEditor;

    public bool IsNew { get; private set; }

    // ---- 清單與詳細區標頭的顯示文字（唯讀）----

    /// <summary>清單上方的 Caption：「我的連線 · N」。</summary>
    public string ListTitle => string.Format(CultureInfo.CurrentCulture, Strings.Conn_ListTitle, Items.Count);

    /// <summary>詳細區標頭的名稱：已儲存的名稱（輸入中的名稱不會即時改標頭）；新連線是預設名稱。</summary>
    public string HeaderTitle => IsNew ? Strings.Conn_NewName : _snapshot.Name;

    /// <summary>詳細區標頭的供應商 Caption：目前選的供應商格式。</summary>
    public string HeaderProvider => Format.Text;

    public string HeaderInitial => ProviderInitial.Of(HeaderTitle, _snapshot.Format);

    /// <summary>目前編輯的連線最近一次測試連線的狀態（標頭的狀態徽章）；新連線或沒有測試過是「尚未確認」。</summary>
    public ConnectionStatus EditorStatus => _editingId is { } id ? _status.Get(id).Status : ConnectionStatus.Unknown;

    public string EditorStatusText => ConnectionStatusText.Of(EditorStatus);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NameError), nameof(CanSave), nameof(IsDirty))]
    private string _name = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave), nameof(IsDirty), nameof(HeaderProvider))]
    private FormatOption _format = FormatOption.All[1];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BaseUrlError), nameof(BaseUrlWarning), nameof(CanSave), nameof(IsDirty))]
    private string _baseUrl = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private string _newApiKey = "";

    [ObservableProperty]
    private bool _revealKey;

    // 金鑰狀態：是否已存、末 4 碼、是否正在輸入新金鑰
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(KeyStatusText), nameof(ShowKeyInput), nameof(ShowKeyStatus))]
    private bool _hasStoredKey;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(KeyStatusText))]
    private string? _keyHint;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowKeyInput), nameof(ShowKeyStatus))]
    private bool _replacingKey;

    public bool ShowKeyInput => !HasStoredKey || ReplacingKey;

    public bool ShowKeyStatus => HasStoredKey;

    public string KeyStatusText => HasStoredKey
        ? (KeyHint is { } hint ? string.Format(CultureInfo.CurrentCulture, Strings.Conn_KeySetHint, hint) : Strings.Conn_KeySet)
        : Strings.Conn_KeyNotSet;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    public bool HasNotice => Notice is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public bool HasSaveError => SaveError is not null;

    /// <summary>CanSave 一變，儲存按鈕的啟用狀態就要跟著變（命令不會自己追蹤 CanSave）。</summary>
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(CanSave))
        {
            SaveCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasFieldErrors));
        }
    }

    /// <summary>有欄位錯誤：儲存按鈕停用時，動作列旁邊用文字說明原因（錯誤可能在收合的「進階」裡）。</summary>
    public bool HasFieldErrors => HasEditor && (NameError is not null || BaseUrlError is not null);

    // ---- 驗證 ----

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? Strings.Conn_NameRequired : null;

    public string? BaseUrlError => TryParseUrl(BaseUrl, out _) ? null : Strings.Conn_UrlInvalid;

    /// <summary>http 連到非本機、非內部網路的位址：金鑰會以明文在網路上傳送。只警告，不擋。</summary>
    public string? BaseUrlWarning => TryParseUrl(BaseUrl, out var uri) && uri.Scheme == Uri.UriSchemeHttp && !IsLocalOrPrivate(uri.Host)
        ? Strings.Conn_UrlInsecure
        : null;

    public bool CanSave => HasEditor && NameError is null && BaseUrlError is null && !IsSaving;

    public bool IsDirty => HasEditor && (Name != _snapshot.Name || Format.Format != _snapshot.Format
        || BaseUrl != _snapshot.BaseUrl || NewApiKey.Length > 0);

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaving;

    // ---- 測試連線 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTestResult), nameof(ShowTestOk), nameof(ShowTestFailure))]
    private string? _testResultText;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowTestOk), nameof(ShowTestFailure))]
    private bool _testSucceeded;

    public bool ShowTestOk => HasTestResult && TestSucceeded;

    public bool ShowTestFailure => HasTestResult && !TestSucceeded;

    [ObservableProperty]
    private bool _isTesting;

    public bool HasTestResult => TestResultText is not null;

    partial void OnFormatChanged(FormatOption value)
    {
        // 新增連線時，Base URL 還沒改過就跟著格式換範本
        if (IsNew && !_loading && (BaseUrl.Length == 0 || BaseUrl == AnthropicTemplateUrl || BaseUrl == OpenAITemplateUrl))
            BaseUrl = TemplateUrl(value.Format);
    }

    // ---- 導覽 ----

    /// <summary>從外部（缺金鑰流程）導到某個連線。</summary>
    public async Task NavigateAsync(string? connectionId, bool focusApiKey, string? notice)
    {
        Notice = notice;
        var item = Items.FirstOrDefault(i => i.Id == connectionId);
        if (item is not null && !await SelectAsync(item))
            return;
        if (focusApiKey && HasEditor)
        {
            ReplacingKey = true;
            FocusApiKeyRequested?.Invoke();
        }
    }

    public async Task<bool> SelectAsync(ConnectionListItem? item)
    {
        if (ReferenceEquals(item, _selectedItem) && HasEditor)
            return true;
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return false;

        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        if (item is null)
            ClearEditor();
        else
            await LoadAsync(_catalog.Current.Connections.First(c => c.Id == item.Id));
        return true;
    }

    [RelayCommand]
    private async Task NewConnectionAsync()
    {
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return;

        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        StartEditing(ConnectionSettings.NewId(), isNew: true, Strings.Conn_NewName, ProviderFormat.OpenAICompatible,
            OpenAITemplateUrl, hasKey: false, hint: null);
    }

    private async Task LoadAsync(ConnectionSettings connection)
    {
        var hasKey = await _catalog.HasApiKeyAsync(connection.Id);
        StartEditing(connection.Id, isNew: false, connection.Name, connection.Format, connection.BaseUrl, hasKey, connection.ApiKeyHint);
    }

    private void StartEditing(string id, bool isNew, string name, ProviderFormat format, string baseUrl, bool hasKey, string? hint)
    {
        _loading = true;
        try
        {
            _editingId = id;
            IsNew = isNew;
            _snapshot = (name, format, baseUrl);
            Name = name;
            Format = FormatOption.All.First(f => f.Format == format);
            BaseUrl = baseUrl;
            NewApiKey = "";
            RevealKey = false;
            HasStoredKey = hasKey;
            KeyHint = hint;
            ReplacingKey = false;
            SaveError = null;
            TestResultText = null;
            TestSucceeded = false;
        }
        finally
        {
            _loading = false;
        }
        NotifyEditorStateChanged();
    }

    private void ClearEditor()
    {
        _editingId = null;
        IsNew = false;
        _snapshot = ("", ProviderFormat.OpenAICompatible, "");
        Name = "";
        BaseUrl = "";
        NewApiKey = "";
        TestResultText = null;
        NotifyEditorStateChanged();
    }

    private void NotifyEditorStateChanged()
    {
        OnPropertyChanged(nameof(HasEditor));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(BaseUrlError));
        OnPropertyChanged(nameof(BaseUrlWarning));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderProvider));
        OnPropertyChanged(nameof(HeaderInitial));
        OnPropertyChanged(nameof(EditorStatus));
        OnPropertyChanged(nameof(EditorStatusText));
        DeleteCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
    }

    private void RefreshItems()
    {
        var settings = _catalog.Current;
        var selectedId = _selectedItem?.Id;
        _rebuildingItems = true;
        try
        {
            Items.Clear();
            foreach (var c in settings.Connections)
                Items.Add(new ConnectionListItem(c.Id, c.Name, c.Format, settings.Models.Count(m => m.ConnectionId == c.Id), _status.Get(c.Id).Status));
        }
        finally
        {
            _rebuildingItems = false;
        }

        _selectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
        OnPropertyChanged(nameof(SelectedItem));
    }

    // ---- 金鑰 ----

    [RelayCommand]
    private void ReplaceKey()
    {
        ReplacingKey = true;
        FocusApiKeyRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelReplaceKey()
    {
        NewApiKey = "";
        ReplacingKey = false;
    }

    [RelayCommand(CanExecute = nameof(CanClearKey))]
    private async Task ClearKeyAsync()
    {
        var id = _editingId!;
        var message = string.Format(CultureInfo.CurrentCulture, Strings.Confirm_ClearKey, Name);
        if (!await _confirm.ConfirmDestructiveAsync(message, Strings.Confirm_Clear))
            return;

        await RunCatalogAsync(async () =>
        {
            await _catalog.ClearApiKeyAsync(id);
            HasStoredKey = false;
            KeyHint = null;
            ReplacingKey = false;
            _status.Remove(id);
        });
    }

    private bool CanClearKey() => HasEditor && !IsNew && HasStoredKey;

    partial void OnHasStoredKeyChanged(bool value) => ClearKeyCommand.NotifyCanExecuteChanged();

    // ---- 儲存／刪除 ----

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var id = _editingId!;
        var connection = new ConnectionSettings(id, Name.Trim(), Format.Format, BaseUrl.Trim());
        var key = NewApiKey.Trim() is { Length: > 0 } typed ? new SecretValue(typed) : null;

        var saved = false;
        IsSaving = true;
        try
        {
            saved = await RunCatalogAsync(async () =>
            {
                await _catalog.SaveConnectionAsync(connection, key);
                _status.Remove(id);   // 設定改了，舊的測試結果不準了
            });
        }
        finally
        {
            IsSaving = false;
        }

        if (!saved)
            return;

        var stored = _catalog.Current.Connections.First(c => c.Id == id);
        _selectedItem = Items.FirstOrDefault(i => i.Id == id);
        OnPropertyChanged(nameof(SelectedItem));
        StartEditing(id, isNew: false, stored.Name, stored.Format, stored.BaseUrl, await _catalog.HasApiKeyAsync(id), stored.ApiKeyHint);
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task DeleteAsync()
    {
        var id = _editingId!;
        if (IsNew)
        {
            // 還沒儲存的新連線：直接放棄
            ClearEditor();
            return;
        }

        var models = _catalog.Current.Models.Count(m => m.ConnectionId == id);
        var message = models > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Confirm_DeleteConnection, Name, models)
            : string.Format(CultureInfo.CurrentCulture, Strings.Confirm_DeleteConnectionNoModels, Name);
        if (!await _confirm.ConfirmDestructiveAsync(message, Strings.Confirm_Delete))
            return;

        if (await RunCatalogAsync(async () =>
            {
                await _catalog.DeleteConnectionAsync(id);
                _status.Remove(id);
            }))
        {
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            ClearEditor();
        }
    }

    /// <summary>執行目錄操作；已知的失敗轉成表單上的錯誤文字，回傳有沒有成功。</summary>
    private async Task<bool> RunCatalogAsync(Func<Task> action)
    {
        SaveError = null;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SaveError = ex.Message;   // 我們自己丟的、不含金鑰的繁中訊息
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretStoreException)
        {
            SaveError = Strings.Settings_SaveFailed;
        }
        return false;
    }

    // ---- 測試連線 ----

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        var connection = new ConnectionSettings(_editingId!, Name.Trim(), Format.Format, BaseUrl.Trim());
        var key = NewApiKey.Trim() is { Length: > 0 } typed ? new SecretValue(typed) : null;

        using var cts = new CancellationTokenSource();
        _testCts = cts;
        IsTesting = true;
        TestResultText = null;
        try
        {
            var result = await _tester.TestAsync(connection, key, cts.Token);
            TestSucceeded = result.Success;
            TestResultText = result.Success
                ? (result.ModelIds.Count > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.Conn_TestOk, result.ModelIds.Count)
                    : Strings.Conn_TestOkNoList)
                : result.Failure!.Message;
            _status.Set(connection.Id, result);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            TestResultText = null;
        }
        finally
        {
            _testCts = null;
            IsTesting = false;
        }
    }

    private bool CanTest() => HasEditor && BaseUrlError is null && !IsTesting;

    partial void OnIsTestingChanged(bool value)
    {
        TestCommand.NotifyCanExecuteChanged();
        CancelTestCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsTesting))]
    private void CancelTest() => _testCts?.Cancel();

    // ---- 小工具 ----

    public static string TemplateUrl(ProviderFormat format) =>
        format == ProviderFormat.Anthropic ? AnthropicTemplateUrl : OpenAITemplateUrl;

    private static bool TryParseUrl(string text, out Uri uri)
    {
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    /// <summary>本機、內部網路（私有位址、.local、沒有點的主機名）視為不需要警告。</summary>
    public static bool IsLocalOrPrivate(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                return true;
            var b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
        }
        return !host.Contains('.');
    }
}
