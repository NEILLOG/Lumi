using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定頁「工具」分段的「MCP 伺服器」：清單＋詳細表單（M16 16F）。版面仿照 <see cref="ConnectionsViewModel"/>。
/// 祕密（環境變數與標頭的值）只在儲存、測試時讀寫 <see cref="IModelCatalog"/>，已儲存的值永遠不讀回畫面。
/// </summary>
public partial class McpServersViewModel : ObservableObject
{
    /// <summary>會改變表單內容的屬性（改了就清掉測試結果，並重新計算是否有未儲存的變更）。</summary>
    private static readonly HashSet<string> FormFields =
    [
        nameof(Name), nameof(IsStdio), nameof(Command), nameof(ArgsText), nameof(WorkingDirectory),
        nameof(Url), nameof(Key), nameof(TimeoutText), nameof(Enabled), nameof(AlsoInSprite),
    ];

    /// <summary>所有依表單內容計算的屬性；變更時一併通知畫面。</summary>
    private static readonly string[] ComputedNames =
    [
        nameof(IsHttp), nameof(NameError), nameof(CommandError), nameof(UrlError), nameof(TransportError), nameof(KeyError),
        nameof(TimeoutError), nameof(HasFieldErrors), nameof(AdvancedHasError), nameof(CanSave), nameof(IsDirty),
        nameof(HasEditor), nameof(ShowPlaceholder), nameof(IsNew), nameof(HeaderTitle), nameof(HeaderInitial),
        nameof(HeaderSubtitle), nameof(HasEditorStatus), nameof(EditorStatusText), nameof(EditorIsReady),
        nameof(EditorIsConnecting), nameof(EditorIsFailed), nameof(EditorIsDisabled), nameof(EditorError),
        nameof(ListTitle), nameof(IsListEmpty),
    ];

    private readonly IModelCatalog _catalog;
    private readonly IMcpToolSource _mcp;
    private readonly IConfirmService _confirm;

    private string? _editingId;            // 正在編輯的伺服器 Id（新增時是預先產生的 Id）
    private McpServerSettings? _stored;    // 已儲存的版本（新增時是 null）
    private string _baseline = "";         // 載入時表單的內容，用來判斷有沒有未儲存的變更
    private bool _loading;
    private bool _keyEdited;               // 使用者改過代號就不再跟著名稱走
    private bool _settingKey;
    private McpServerListItem? _selectedItem;
    private bool _rebuildingItems;         // 重建清單時，清單控制項會把選取寫成 null：這時不能當成使用者取消選取

    public McpServersViewModel(IModelCatalog catalog, IMcpToolSource mcp, IConfirmService confirm, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _mcp = mcp;
        _confirm = confirm;
        RefreshItems();
        catalog.Changed += (_, _) => dispatcher.Post(RefreshItems);
        mcp.Changed += (_, _) => dispatcher.Post(RefreshItems);
    }

    public ObservableCollection<McpServerListItem> Items { get; } = [];

    public ObservableCollection<McpSecretRowViewModel> EnvRows { get; } = [];

    public ObservableCollection<McpSecretRowViewModel> HeaderRows { get; } = [];

    /// <summary>頁首的主要動作（新增伺服器）。</summary>
    public IAsyncRelayCommand HeaderPrimaryActionCommand => NewServerCommand;

    // ---- 清單 ----

    /// <summary>清單上方的 Caption：「我的伺服器 · N」。</summary>
    public string ListTitle => string.Format(CultureInfo.CurrentCulture, Strings.Mcp_ListTitle, Items.Count);

    public bool IsListEmpty => Items.Count == 0;

    public McpServerListItem? SelectedItem
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

    // ---- 編輯區的狀態 ----

    public bool HasEditor => _editingId is not null;

    public bool ShowPlaceholder => !HasEditor && !IsImporting;

    public bool IsNew { get; private set; }

    /// <summary>「從 JSON 匯入」的面板；有值時詳細區顯示匯入畫面，取代表單。</summary>
    [ObservableProperty]
    private McpImportViewModel? _import;

    public bool IsImporting => Import is not null;

    partial void OnImportChanged(McpImportViewModel? value)
    {
        OnPropertyChanged(nameof(IsImporting));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    /// <summary>詳細區標頭的名稱：已儲存的名稱（輸入中的名稱不會即時改標頭）；新伺服器是預設名稱。</summary>
    public string HeaderTitle => IsNew ? Strings.Mcp_NewName : _stored?.Name ?? "";

    public string HeaderInitial => McpServerListItem.InitialOf(HeaderTitle);

    /// <summary>詳細區標頭的連線方式 Caption（跟著目前選的連線方式）。</summary>
    public string HeaderSubtitle => IsStdio ? Strings.Mcp_TransportStdio : Strings.Mcp_TransportHttp;

    /// <summary>編輯中的伺服器在清單上的那一列（新增中的沒有）。</summary>
    private McpServerListItem? EditorItem => HasEditor && !IsNew ? Items.FirstOrDefault(i => i.Id == _editingId) : null;

    public bool HasEditorStatus => EditorItem is not null;

    public string EditorStatusText => EditorItem?.StatusText ?? "";

    public bool EditorIsReady => EditorItem?.IsReady == true;

    public bool EditorIsConnecting => EditorItem?.IsConnecting == true;

    public bool EditorIsFailed => EditorItem?.IsFailed == true;

    public bool EditorIsDisabled => EditorItem?.IsDisabled == true;

    /// <summary>伺服器回報的錯誤原因（連線失敗時顯示在狀態區）。</summary>
    public string? EditorError => EditorItem?.Error;

    // ---- 表單欄位 ----

    [ObservableProperty]
    private string _name = "";

    /// <summary>true＝本機指令（stdio）；false＝遠端 HTTP。</summary>
    [ObservableProperty]
    private bool _isStdio = true;

    [ObservableProperty]
    private string _command = "";

    /// <summary>參數，多行文字框，一行一個。</summary>
    [ObservableProperty]
    private string _argsText = "";

    [ObservableProperty]
    private string _workingDirectory = "";

    [ObservableProperty]
    private string _url = "";

    /// <summary>工具名稱用的代號（進階）。新增時跟著名稱產生，使用者改過就不再跟著走。</summary>
    [ObservableProperty]
    private string _key = "";

    [ObservableProperty]
    private string _timeoutText = "60";

    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private bool _alsoInSprite;

    public bool IsHttp
    {
        get => !IsStdio;
        set => IsStdio = !value;
    }

    // ---- 驗證（規則與文字在 McpServerRules，這裡只接上） ----

    public string? NameError => McpServerRules.ValidateName(Name);

    public string? CommandError => IsStdio ? McpServerRules.ValidateCommand(Command) : null;

    public string? UrlError => IsStdio ? null : McpServerRules.ValidateUrl(Url);

    /// <summary>連線方式欄位的錯誤（指令或 URL），測試連線用它判斷能不能按。</summary>
    public string? TransportError => CommandError ?? UrlError;

    public string? KeyError => McpServerRules.ValidateKey(Key, OtherKeys);

    public string? TimeoutError => McpServerRules.ValidateTimeout(TimeoutText);

    /// <summary>進階區段裡有錯誤（代號、逾時）：畫面會自動展開「進階」。</summary>
    public bool AdvancedHasError => KeyError is not null || TimeoutError is not null;

    /// <summary>目前連線方式用到的列（stdio 是環境變數，HTTP 是標頭）。</summary>
    private ObservableCollection<McpSecretRowViewModel> ActiveRows => IsStdio ? EnvRows : HeaderRows;

    public bool HasFieldErrors => NameError is not null || TransportError is not null || KeyError is not null
        || TimeoutError is not null || ActiveRows.Any(r => r.NameError is not null);

    public bool CanSave => HasEditor && !IsSaving && !HasFieldErrors;

    public bool IsDirty => HasEditor && Signature() != _baseline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public bool HasSaveError => SaveError is not null;

    [ObservableProperty]
    private bool _isSaving;

    // ---- 測試連線 ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult), nameof(ShowTestOk), nameof(ShowTestFailure), nameof(ShowTestTools))]
    private string? _testResultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTestOk), nameof(ShowTestFailure), nameof(ShowTestTools))]
    private bool _testSucceeded;

    /// <summary>測試成功時前 10 個工具名稱（Caption）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTestTools))]
    private string? _testToolNames;

    [ObservableProperty]
    private bool _isTesting;

    public bool HasTestResult => TestResultText is not null;

    public bool ShowTestOk => HasTestResult && TestSucceeded;

    public bool ShowTestFailure => HasTestResult && !TestSucceeded;

    public bool ShowTestTools => ShowTestOk && TestToolNames is not null;

    // ---- 命令 ----

    [RelayCommand]
    private async Task NewServerAsync()
    {
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return;

        Import = null;
        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        StartNew();
    }

    [RelayCommand]
    private async Task OpenImportAsync()
    {
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return;

        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        ClearEditor();

        var import = new McpImportViewModel(_catalog);
        import.Imported += OnImported;
        import.Cancelled += () => Import = null;
        Import = import;
    }

    [RelayCommand]
    private void AddEnv()
    {
        EnvRows.Add(NewEnvRow("", isSaved: false));
        RowChanged();
    }

    [RelayCommand]
    private void AddHeader()
    {
        HeaderRows.Add(NewHeaderRow("", isSaved: false));
        RowChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var id = _editingId!;
        var server = BuildServer(id);

        IsSaving = true;
        try
        {
            var saved = await RunCatalogAsync(async () =>
            {
                var (secrets, changed) = await ComputeSecretsAsync(id, CancellationToken.None);
                await _catalog.SaveMcpServerAsync(server, changed ? secrets : null);
            });
            if (!saved)
                return;
        }
        finally
        {
            IsSaving = false;
        }

        RefreshItems();
        var stored = _catalog.Current.McpServers.First(s => s.Id == id);
        _selectedItem = Items.FirstOrDefault(i => i.Id == id);
        OnPropertyChanged(nameof(SelectedItem));
        LoadStored(stored);
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task DeleteAsync()
    {
        var id = _editingId!;
        if (IsNew)
        {
            // 還沒儲存的新伺服器：直接放棄
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            ClearEditor();
            return;
        }

        var message = string.Format(CultureInfo.CurrentCulture, Strings.Mcp_DeleteConfirm, _stored?.Name ?? Name);
        if (!await _confirm.ConfirmDestructiveAsync(message, Strings.Confirm_Delete))
            return;

        if (await RunCatalogAsync(() => _catalog.DeleteMcpServerAsync(id)))
        {
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            ClearEditor();
            RefreshItems();
        }
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        var id = _editingId!;
        var server = BuildServer(id);

        IsTesting = true;
        ClearTestResult();
        try
        {
            var (secrets, _) = await ComputeSecretsAsync(id, CancellationToken.None);
            var result = await _mcp.TestAsync(server, secrets, CancellationToken.None);
            TestSucceeded = result.Success;
            if (result.Success)
            {
                TestResultText = string.Format(CultureInfo.CurrentCulture, Strings.Mcp_TestOk, result.ToolNames.Count);
                TestToolNames = result.ToolNames.Count > 0 ? string.Join("、", result.ToolNames.Take(10)) : null;
            }
            else
            {
                TestResultText = result.Error ?? Strings.Mcp_StatusFailed;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretStoreException)
        {
            // 讀不到祕密時不測試，原因只寫我們自己的文字（不含祕密）
            TestSucceeded = false;
            TestResultText = Strings.ApiKey_Unreadable;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest() => HasEditor && !IsTesting && TransportError is null;

    // ---- 選取與載入 ----

    /// <summary>切換到另一個伺服器（或取消選取）；有未儲存的變更時先詢問，取消則留在原處。</summary>
    public async Task<bool> SelectAsync(McpServerListItem? item)
    {
        if (ReferenceEquals(item, _selectedItem) && HasEditor)
            return true;
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return false;

        Import = null;
        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        if (item is null)
        {
            ClearEditor();
            return true;
        }

        var server = _catalog.Current.McpServers.FirstOrDefault(s => s.Id == item.Id);
        if (server is null)
            ClearEditor();
        else
            LoadStored(server);
        return true;
    }

    private void OnImported(string firstId)
    {
        Import = null;
        RefreshItems();
        _ = SelectAsync(Items.FirstOrDefault(i => i.Id == firstId));
    }

    private void StartNew()
    {
        _loading = true;
        try
        {
            _editingId = McpServerSettings.NewId();
            IsNew = true;
            _stored = null;
            _keyEdited = false;
            Name = Strings.Mcp_NewName;
            IsStdio = true;
            Command = "";
            ArgsText = "";
            WorkingDirectory = "";
            Url = "";
            TimeoutText = "60";
            Enabled = true;
            AlsoInSprite = false;
            EnvRows.Clear();
            HeaderRows.Clear();
            SetKeyFromName();
            SaveError = null;
            ClearTestResult();
        }
        finally
        {
            _loading = false;
        }
        FinishLoad();
    }

    private void LoadStored(McpServerSettings server)
    {
        _loading = true;
        try
        {
            _editingId = server.Id;
            IsNew = false;
            _stored = server;
            Name = server.Name;
            IsStdio = server.Transport == McpTransportKind.Stdio;
            Command = server.Command ?? "";
            ArgsText = string.Join("\n", server.Args);
            WorkingDirectory = server.WorkingDirectory ?? "";
            Url = server.Url ?? "";
            Key = server.Key;
            TimeoutText = server.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            Enabled = server.Enabled;
            AlsoInSprite = server.AlsoInSprite;
            EnvRows.Clear();
            foreach (var name in server.EnvNames)
                EnvRows.Add(NewEnvRow(name, isSaved: true));
            HeaderRows.Clear();
            foreach (var name in server.HeaderNames)
                HeaderRows.Add(NewHeaderRow(name, isSaved: true));
            SaveError = null;
            ClearTestResult();
        }
        finally
        {
            _loading = false;
        }
        FinishLoad();
    }

    private void ClearEditor()
    {
        _editingId = null;
        IsNew = false;
        _stored = null;
        _loading = true;
        try
        {
            Name = "";
            IsStdio = true;
            Command = "";
            ArgsText = "";
            WorkingDirectory = "";
            Url = "";
            Key = "";
            TimeoutText = "60";
            Enabled = true;
            AlsoInSprite = false;
            EnvRows.Clear();
            HeaderRows.Clear();
            SaveError = null;
            ClearTestResult();
        }
        finally
        {
            _loading = false;
        }
        _baseline = "";
        RaiseComputed();
    }

    private void FinishLoad()
    {
        _baseline = Signature();
        RaiseComputed();
    }

    /// <summary>重建清單（伺服器設定或連線狀態改變時）。選取的伺服器會依 Id 保留。</summary>
    private void RefreshItems()
    {
        var settings = _catalog.Current;
        var statuses = new Dictionary<string, McpServerStatus>();
        foreach (var status in _mcp.GetStatuses())
            statuses[status.ServerId] = status;

        var selectedId = _selectedItem?.Id;
        _rebuildingItems = true;
        try
        {
            Items.Clear();
            foreach (var server in settings.McpServers)
            {
                statuses.TryGetValue(server.Id, out var status);
                var state = !server.Enabled ? McpServerState.Disabled : (status?.State ?? McpServerState.Connecting);
                Items.Add(new McpServerListItem(server.Id, server.Name, server.Transport, server.Command, server.Url,
                    state, status?.ToolCount ?? 0, status?.Error));
            }
        }
        finally
        {
            _rebuildingItems = false;
        }

        _selectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
        OnPropertyChanged(nameof(SelectedItem));
        RaiseComputed();
    }

    // ---- 表單變更 ----

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        SaveCommand?.NotifyCanExecuteChanged();
        TestCommand?.NotifyCanExecuteChanged();
        DeleteCommand?.NotifyCanExecuteChanged();
        if (!_loading && e.PropertyName is { } name && FormFields.Contains(name))
            OnFormChanged();
    }

    partial void OnNameChanged(string value)
    {
        // 新伺服器的代號跟著名稱走，直到使用者自己改代號
        if (!_loading && IsNew && !_keyEdited)
            SetKeyFromName();
    }

    partial void OnKeyChanged(string value)
    {
        if (!_loading && !_settingKey)
            _keyEdited = true;
    }

    private void OnFormChanged()
    {
        ClearTestResult();
        RaiseComputed();
    }

    /// <summary>名稱的錯誤（重複檢查要看其他列），列的改變都經過這裡。</summary>
    private void RowChanged()
    {
        if (_loading)
            return;
        foreach (var row in EnvRows.Concat(HeaderRows))
            row.RefreshNameError();
        OnFormChanged();
    }

    private void SetKeyFromName()
    {
        _settingKey = true;
        try
        {
            Key = McpToolNaming.KeyFromName(Name, OtherKeys);
        }
        finally
        {
            _settingKey = false;
        }
    }

    private void ClearTestResult()
    {
        TestResultText = null;
        TestSucceeded = false;
        TestToolNames = null;
    }

    private void RaiseComputed()
    {
        foreach (var name in ComputedNames)
            OnPropertyChanged(name);
    }

    // ---- 列 ----

    private McpSecretRowViewModel NewEnvRow(string name, bool isSaved) =>
        new(name, isSaved, EnvNameError, row => { EnvRows.Remove(row); RowChanged(); }, RowChanged);

    private McpSecretRowViewModel NewHeaderRow(string name, bool isSaved) =>
        new(name, isSaved, HeaderNameError, row => { HeaderRows.Remove(row); RowChanged(); }, RowChanged);

    private string? EnvNameError(McpSecretRowViewModel row) =>
        McpServerRules.ValidateEnvName(row.Name.Trim()) ?? DuplicateError(EnvRows, row);

    private string? HeaderNameError(McpSecretRowViewModel row) =>
        McpServerRules.ValidateHeaderName(row.Name.Trim()) ?? DuplicateError(HeaderRows, row);

    /// <summary>環境變數與標頭的名稱都不分大小寫（Windows 的環境變數、HTTP 標頭）。</summary>
    private static string? DuplicateError(IEnumerable<McpSecretRowViewModel> rows, McpSecretRowViewModel row)
    {
        var name = row.Name.Trim();
        return rows.Any(other => !ReferenceEquals(other, row) && string.Equals(other.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            ? Strings.Mcp_SecretNameDuplicate
            : null;
    }

    // ---- 儲存與祕密 ----

    /// <summary>其他伺服器的代號（這個伺服器的代號不能和它們重複）。</summary>
    private IEnumerable<string> OtherKeys => _catalog.Current.McpServers.Where(s => s.Id != _editingId).Select(s => s.Key);

    private McpServerSettings BuildServer(string id)
    {
        var stdio = IsStdio;
        var timeout = int.TryParse(TimeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) ? seconds : 60;
        var workingDirectory = WorkingDirectory.Trim();

        IReadOnlyList<string> args = stdio ? McpServerRules.ParseArgs(ArgsText) : Array.Empty<string>();
        IReadOnlyList<string> envNames = stdio ? EnvRows.Select(r => r.Name.Trim()).ToArray() : Array.Empty<string>();
        IReadOnlyList<string> headerNames = stdio ? Array.Empty<string>() : HeaderRows.Select(r => r.Name.Trim()).ToArray();

        return new McpServerSettings(
            id,
            Key.Trim(),
            Name.Trim(),
            stdio ? McpTransportKind.Stdio : McpTransportKind.Http,
            stdio ? Command.Trim() : null,
            args,
            stdio && workingDirectory.Length > 0 ? workingDirectory : null,
            envNames,
            stdio ? null : Url.Trim(),
            headerNames,
            Enabled,
            AlsoInSprite,
            timeout,
            _stored?.SecretsVersion);
    }

    /// <summary>
    /// 算出這次要寫入的祕密。已儲存的值沒有被更換時沿用目前的值；changed 為 false 時儲存不需要碰祕密（傳 null）。
    /// </summary>
    private async Task<(McpServerSecrets Secrets, bool Changed)> ComputeSecretsAsync(string id, CancellationToken ct)
    {
        var current = await _catalog.GetMcpSecretsAsync(id, ct);
        var env = IsStdio ? ValuesOf(EnvRows, current.Env) : new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = IsStdio ? new Dictionary<string, string>(StringComparer.Ordinal) : ValuesOf(HeaderRows, current.Headers);
        var changed = !SameValues(current.Env, env) || !SameValues(current.Headers, headers);
        return (new McpServerSecrets(env, headers), changed);
    }

    private static Dictionary<string, string> ValuesOf(IEnumerable<McpSecretRowViewModel> rows, IReadOnlyDictionary<string, string> current)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            var keepStored = row.IsSaved && !(row.IsReplacing && row.Value.Length > 0);
            result[name] = keepStored
                ? (current.TryGetValue(name, out var old) ? old : "")
                : row.Value;
        }
        return result;
    }

    private static bool SameValues(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var value) && value == kv.Value);

    /// <summary>表單內容的簽章（判斷有沒有未儲存的變更）。只在記憶體中比較，不寫到任何地方。</summary>
    private string Signature()
    {
        var parts = new List<string?>
        {
            _editingId, Name, IsStdio ? "1" : "0", Command, ArgsText, WorkingDirectory, Url, Key, TimeoutText,
            Enabled ? "1" : "0", AlsoInSprite ? "1" : "0",
        };
        foreach (var row in EnvRows)
            parts.Add($"e|{row.Name}|{row.Value}|{row.IsSaved}|{row.IsReplacing}");
        foreach (var row in HeaderRows)
            parts.Add($"h|{row.Name}|{row.Value}|{row.IsSaved}|{row.IsReplacing}");
        return string.Join('\u001f', parts);
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
            SaveError = ex.Message;   // 我們自己丟的、不含祕密的繁中訊息
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretStoreException)
        {
            SaveError = Strings.Settings_SaveFailed;
        }
        return false;
    }
}
