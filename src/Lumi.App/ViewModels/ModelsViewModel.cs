using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.App.ViewModels;

/// <summary>設定視窗「模型設定」分頁：預設模型、依連線排序的清單、表單。</summary>
public partial class ModelsViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly IConnectionTester _tester;
    private readonly ConnectionStatusCache _status;
    private readonly IConfirmService _confirm;

    private string? _editingId;
    private bool _isNew;
    private bool _loading;
    private bool _refreshing;
    private bool _displayNameFollowsModelId = true;
    private ModelSnapshot _snapshot = ModelSnapshot.Empty;
    private ModelListItem? _selectedItem;
    private CancellationTokenSource? _listCts;

    private sealed record ModelSnapshot(string ConnectionId, string ModelId, string DisplayName, int MaxOutput, int Context, bool ShowThinking, bool ToolsEnabled)
    {
        public static readonly ModelSnapshot Empty = new("", "", "", 0, 0, false, true);
    }

    public ModelsViewModel(IModelCatalog catalog, IConnectionTester tester, ConnectionStatusCache status,
        IConfirmService confirm, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _tester = tester;
        _status = status;
        _confirm = confirm;
        Refresh();
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ListTitle));
        catalog.Changed += (_, _) => dispatcher.Post(Refresh);
        // 清單卡片的連線狀態跟著測試連線更新：只改項目的狀態，不重建清單（重建會讓表單的選取跳掉）
        status.Changed += (_, _) => dispatcher.Post(SyncItemStatuses);
    }

    public ObservableCollection<ModelListItem> Items { get; } = [];

    public ObservableCollection<ModelOption> DefaultOptions { get; } = [];

    public ObservableCollection<ConnectionOption> Connections { get; } = [];

    public ObservableCollection<string> AvailableModelIds { get; } = [];

    public bool CanAddModel => Connections.Count > 0;

    public bool ShowNeedConnectionHint => Connections.Count == 0;

    [ObservableProperty]
    private string? _defaultChangedNotice;

    // ---- 預設模型下拉：改了立即生效 ----

    private ModelOption? _workbenchOption;

    /// <summary>預設模型：工作台與小精靈共用（小精靈不另外選模型）。</summary>
    public ModelOption? WorkbenchOption
    {
        get => _workbenchOption;
        set => _ = SetDefaultAsync(value);
    }

    private async Task SetDefaultAsync(ModelOption? option)
    {
        if (_refreshing || option is null)
            return;
        try
        {
            await _catalog.SetWorkbenchModelAsync(option.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SaveError = ex is IOException or UnauthorizedAccessException ? Strings.Settings_SaveFailed : ex.Message;
            Refresh();   // 讓下拉還原成實際的值
        }
    }

    // ---- 選取 ----

    public ModelListItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_refreshing || ReferenceEquals(value, _selectedItem))
                return;
            _ = SelectAsync(value);
            OnPropertyChanged();
        }
    }

    public bool HasEditor => _editingId is not null;

    public bool ShowPlaceholder => !HasEditor;

    public bool IsNew => _isNew;

    // ---- 清單與詳細區標頭的顯示文字（唯讀）----

    /// <summary>清單上方的 Caption：「我的模型設定 · N」。</summary>
    public string ListTitle => string.Format(CultureInfo.CurrentCulture, Strings.Models_ListTitle, Items.Count);

    /// <summary>詳細區標頭的名稱：已儲存的顯示名稱；新模型設定是「新增模型設定」。</summary>
    public string HeaderTitle => _isNew ? Strings.Models_New : _snapshot.DisplayName;

    /// <summary>詳細區標頭的供應商 Caption：所選連線的名稱。</summary>
    public string HeaderProvider => SelectedConnection?.Name ?? "";

    public string HeaderInitial =>
        ProviderInitial.Of(SelectedConnection?.Name, _catalog.Current.Connections.FirstOrDefault(c => c.Id == SelectedConnection?.Id)?.Format ?? ProviderFormat.OpenAICompatible);

    /// <summary>所選連線最近一次測試連線的狀態（標頭的狀態徽章）。</summary>
    public ConnectionStatus EditorStatus => SelectedConnection is { } c ? _status.Get(c.Id).Status : ConnectionStatus.Unknown;

    public string EditorStatusText => ConnectionStatusText.Of(EditorStatus);

    private void SyncItemStatuses()
    {
        foreach (var item in Items)
        {
            if (_catalog.Current.Models.FirstOrDefault(m => m.Id == item.Id) is { } model)
                item.Status = _status.Get(model.ConnectionId).Status;
        }
        OnPropertyChanged(nameof(EditorStatus));
        OnPropertyChanged(nameof(EditorStatusText));
    }

    // ---- 表單欄位 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(HeaderProvider), nameof(HeaderInitial), nameof(EditorStatus), nameof(EditorStatusText))]
    private ConnectionOption? _selectedConnection;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(ModelIdError))]
    private string _modelId = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(DisplayNameError))]
    private string _displayName = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(MaxOutputError))]
    private decimal? _maxOutputTokens;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(ContextWindowError))]
    private decimal? _contextWindow;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _showThinking;

    /// <summary>送出請求時是否帶工具定義（畫面上的勾選框在 M3 的 3C 加入；先讓編輯器保留這個值，儲存時不會被重設）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _toolsEnabled = true;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public bool HasSaveError => SaveError is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaving;

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
    public bool HasFieldErrors => HasEditor && (SelectedConnection is null || ModelIdError is not null || DisplayNameError is not null
        || MaxOutputError is not null || ContextWindowError is not null);

    // ---- 驗證 ----

    public string? ModelIdError => string.IsNullOrWhiteSpace(ModelId) ? Strings.Models_IdRequired : null;

    public string? DisplayNameError => string.IsNullOrWhiteSpace(DisplayName) ? Strings.Models_NameRequired : null;

    public string? MaxOutputError => IsPositiveInt(MaxOutputTokens) ? null : Strings.Models_NumberInvalid;

    public string? ContextWindowError => IsPositiveInt(ContextWindow) ? null : Strings.Models_NumberInvalid;

    public bool CanSave => HasEditor && SelectedConnection is not null && ModelIdError is null && DisplayNameError is null
        && MaxOutputError is null && ContextWindowError is null && !IsSaving;

    public bool IsDirty => HasEditor && CurrentSnapshot() != _snapshot;

    private ModelSnapshot CurrentSnapshot() => new(SelectedConnection?.Id ?? "", ModelId, DisplayName,
        (int)(MaxOutputTokens ?? 0), (int)(ContextWindow ?? 0), ShowThinking, ToolsEnabled);

    private static bool IsPositiveInt(decimal? value) => value is > 0 and <= int.MaxValue && value == decimal.Truncate(value.Value);

    // ---- 可用模型清單 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasListStatus))]
    private string? _listStatusText;

    public bool HasListStatus => ListStatusText is not null;

    [ObservableProperty]
    private bool _isLoadingList;

    partial void OnModelIdChanged(string value)
    {
        // 顯示名稱還沒被使用者改過，就跟著模型 ID 走
        if (!_loading && _displayNameFollowsModelId)
        {
            _loading = true;
            try { DisplayName = value; }
            finally { _loading = false; }
        }
    }

    partial void OnDisplayNameChanged(string value)
    {
        if (!_loading)
            _displayNameFollowsModelId = value.Length == 0 || value == ModelId;
    }

    partial void OnSelectedConnectionChanged(ConnectionOption? value)
    {
        if (_loading || value is null)
            return;
        // 新增時換連線：預設值跟著格式走（Anthropic／OpenAI 相容差很多）
        if (_isNew)
            ApplyDefaultsFor(value.Id);
        _ = LoadAvailableModelsAsync(forceRefresh: false);
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private Task RefreshModelListAsync() => LoadAvailableModelsAsync(forceRefresh: true);

    private async Task LoadAvailableModelsAsync(bool forceRefresh)
    {
        _listCts?.Cancel();
        AvailableModelIds.Clear();
        ListStatusText = null;
        if (SelectedConnection is null)
            return;

        var connection = _catalog.Current.Connections.FirstOrDefault(c => c.Id == SelectedConnection.Id);
        if (connection is null)
            return;

        var cached = _status.Get(connection.Id);
        if (!forceRefresh && cached.Status == ConnectionStatus.Ok)
        {
            ShowAvailable(cached.ModelIds);
            return;
        }

        using var cts = new CancellationTokenSource();
        _listCts = cts;
        IsLoadingList = true;
        ListStatusText = Strings.Models_ListLoading;
        try
        {
            var result = await _tester.TestAsync(connection, null, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            _status.Set(connection.Id, result);
            if (result.Success && result.ModelIds.Count > 0)
                ShowAvailable(result.ModelIds);
            else
                ListStatusText = Strings.Models_ListUnavailable;
        }
        catch (OperationCanceledException)
        {
            // 被新的請求取代
        }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
            {
                _listCts = null;
                IsLoadingList = false;
            }
        }
    }

    private void ShowAvailable(IReadOnlyList<string> ids)
    {
        AvailableModelIds.Clear();
        foreach (var id in ids)
            AvailableModelIds.Add(id);
        ListStatusText = ids.Count > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Models_ListCount, ids.Count)
            : Strings.Models_ListUnavailable;
    }

    // ---- 清單與導覽 ----

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var settings = _catalog.Current;
            var selectedId = _selectedItem?.Id;
            var editingConnectionId = SelectedConnection?.Id;
            var connectionNames = settings.Connections.ToDictionary(c => c.Id, c => c.Name);
            var connectionFormats = settings.Connections.ToDictionary(c => c.Id, c => c.Format);

            Connections.Clear();
            foreach (var c in settings.Connections)
                Connections.Add(new ConnectionOption(c.Id, c.Name));

            Items.Clear();
            foreach (var m in settings.Models
                         .OrderBy(m => connectionNames.GetValueOrDefault(m.ConnectionId), StringComparer.CurrentCulture)
                         .ThenBy(m => m.DisplayName, StringComparer.CurrentCulture))
            {
                Items.Add(new ModelListItem(m.Id, connectionNames.GetValueOrDefault(m.ConnectionId, ""), m.DisplayName, m.ModelId,
                    m.Id == settings.WorkbenchModelId,
                    connectionFormats.GetValueOrDefault(m.ConnectionId, ProviderFormat.OpenAICompatible), _status.Get(m.ConnectionId).Status));
            }

            DefaultOptions.Clear();
            foreach (var item in Items)
                DefaultOptions.Add(new ModelOption(item.Id, $"{item.DisplayName} · {item.ConnectionName}"));
            _workbenchOption = DefaultOptions.FirstOrDefault(o => o.Id == settings.WorkbenchModelId);
            OnPropertyChanged(nameof(WorkbenchOption));

            // 重建清單時，下拉會把所選連線寫成 null：換成新清單裡同一個連線（不重新載入表單）
            if (editingConnectionId is not null && SelectedConnection?.Id != editingConnectionId)
            {
                var wasLoading = _loading;
                _loading = true;
                try { SelectedConnection = Connections.FirstOrDefault(c => c.Id == editingConnectionId); }
                finally { _loading = wasLoading; }
            }

            _selectedItem = Items.FirstOrDefault(i => i.Id == selectedId);
            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(CanAddModel));
            OnPropertyChanged(nameof(ShowNeedConnectionHint));
            NewModelCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _refreshing = false;
        }
    }

    public async Task<bool> SelectAsync(ModelListItem? item)
    {
        if (ReferenceEquals(item, _selectedItem) && HasEditor)
            return true;
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return false;

        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        if (item is null)
        {
            ClearEditor();
            return true;
        }

        var model = _catalog.Current.Models.First(m => m.Id == item.Id);
        StartEditing(model.Id, isNew: false, model.ConnectionId, model.ModelId, model.DisplayName,
            model.MaxOutputTokens, model.ContextWindow, model.ShowThinking, model.ToolsEnabled,
            displayNameFollows: model.DisplayName == model.ModelId);
        await LoadAvailableModelsAsync(forceRefresh: false);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAddModel))]
    private async Task NewModelAsync()
    {
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return;

        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        var connection = _catalog.Current.Connections[0];
        var (maxOutput, context, thinking) = DefaultsFor(connection.Format);
        StartEditing(ModelSettings.NewId(), isNew: true, connection.Id, "", "", maxOutput, context, thinking, toolsEnabled: true, displayNameFollows: true);
        await LoadAvailableModelsAsync(forceRefresh: false);
    }

    private void StartEditing(string id, bool isNew, string connectionId, string modelId, string displayName,
        int maxOutput, int context, bool showThinking, bool toolsEnabled, bool displayNameFollows)
    {
        _loading = true;
        try
        {
            _editingId = id;
            _isNew = isNew;
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == connectionId);
            ModelId = modelId;
            DisplayName = displayName;
            MaxOutputTokens = maxOutput;
            ContextWindow = context;
            ShowThinking = showThinking;
            ToolsEnabled = toolsEnabled;
            _displayNameFollowsModelId = displayNameFollows;
            _snapshot = new ModelSnapshot(connectionId, modelId, displayName, maxOutput, context, showThinking, toolsEnabled);
            SaveError = null;
        }
        finally
        {
            _loading = false;
        }
        NotifyEditorStateChanged();
    }

    private void ClearEditor()
    {
        _listCts?.Cancel();
        _editingId = null;
        _isNew = false;
        _snapshot = ModelSnapshot.Empty;
        AvailableModelIds.Clear();
        ListStatusText = null;
        NotifyEditorStateChanged();
    }

    private void ApplyDefaultsFor(string connectionId)
    {
        var connection = _catalog.Current.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (connection is null)
            return;
        var (maxOutput, context, thinking) = DefaultsFor(connection.Format);
        MaxOutputTokens = maxOutput;
        ContextWindow = context;
        ShowThinking = thinking;
    }

    /// <summary>新增模型時的預設值依連線格式；使用者依實際模型自己調整（M2 不內建模型規格表）。</summary>
    public static (int MaxOutput, int Context, bool ShowThinking) DefaultsFor(ProviderFormat format) =>
        format == ProviderFormat.Anthropic ? (64_000, 1_000_000, true) : (4_096, 128_000, false);

    private void NotifyEditorStateChanged()
    {
        OnPropertyChanged(nameof(HasEditor));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ModelIdError));
        OnPropertyChanged(nameof(DisplayNameError));
        OnPropertyChanged(nameof(MaxOutputError));
        OnPropertyChanged(nameof(ContextWindowError));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderProvider));
        OnPropertyChanged(nameof(HeaderInitial));
        OnPropertyChanged(nameof(EditorStatus));
        OnPropertyChanged(nameof(EditorStatusText));
        DeleteCommand.NotifyCanExecuteChanged();
        RefreshModelListCommand.NotifyCanExecuteChanged();
    }

    // ---- 儲存／刪除 ----

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var id = _editingId!;
        var model = new ModelSettings(id, SelectedConnection!.Id, ModelId.Trim(), DisplayName.Trim(),
            (int)MaxOutputTokens!.Value, (int)ContextWindow!.Value, ShowThinking, ToolsEnabled);

        // 新增模型時，原本的預設模型若已無法使用，就把新模型設成預設（22B）
        var replacesDefault = _isNew && await IsDefaultUnusableAsync();

        IsSaving = true;
        try
        {
            if (!await RunAsync(() => _catalog.SaveModelAsync(model)))
                return;
        }
        finally
        {
            IsSaving = false;
        }

        if (replacesDefault && await RunAsync(() => _catalog.SetWorkbenchModelAsync(id)))
            DefaultChangedNotice = string.Format(CultureInfo.CurrentCulture, Strings.Models_DefaultReplaced, model.DisplayName);

        // 第一個模型設定時，預設模型會由目錄自動指定
        var item = Items.FirstOrDefault(i => i.Id == id);
        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        StartEditing(id, isNew: false, model.ConnectionId, model.ModelId, model.DisplayName, model.MaxOutputTokens,
            model.ContextWindow, model.ShowThinking, model.ToolsEnabled, displayNameFollows: model.DisplayName == model.ModelId);
    }

    /// <summary>預設模型存在但無法使用：找不到，或它的連線沒有金鑰。一個模型都沒有（沒有預設）不算。</summary>
    private async Task<bool> IsDefaultUnusableAsync()
    {
        var workbenchId = _catalog.Current.WorkbenchModelId;
        if (workbenchId is null)
            return false;
        var resolved = _catalog.Resolve(workbenchId);
        if (resolved is null)
            return true;
        try
        {
            return !await _catalog.HasApiKeyAsync(resolved.Connection.Id);
        }
        catch (SecretStoreException)
        {
            return false;   // 讀不出來不代表模型壞了，不動預設
        }
    }

    [RelayCommand(CanExecute = nameof(HasEditor))]
    private async Task DeleteAsync()
    {
        var id = _editingId!;
        if (_isNew)
        {
            _selectedItem = null;
            OnPropertyChanged(nameof(SelectedItem));
            ClearEditor();
            return;
        }

        var message = string.Format(CultureInfo.CurrentCulture, Strings.Confirm_DeleteModel, DisplayName);
        if (!await _confirm.ConfirmDestructiveAsync(message, Strings.Confirm_Delete))
            return;

        var before = _catalog.Current.WorkbenchModelId;
        if (!await RunAsync(() => _catalog.DeleteModelAsync(id)))
            return;

        _selectedItem = null;
        OnPropertyChanged(nameof(SelectedItem));
        ClearEditor();

        var after = _catalog.Current.WorkbenchModelId;
        if (before == id)
        {
            var name = _catalog.Current.Models.FirstOrDefault(m => m.Id == after)?.DisplayName ?? Strings.Models_NoDefault;
            DefaultChangedNotice = string.Format(CultureInfo.CurrentCulture, Strings.Models_DefaultChanged, name);
        }
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        SaveError = null;
        DefaultChangedNotice = null;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SaveError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretStoreException)
        {
            SaveError = Strings.Settings_SaveFailed;
        }
        return false;
    }
}
