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
        catalog.Changed += (_, _) => dispatcher.Post(Refresh);
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
    private ModelOption? _spriteOption;

    public ModelOption? WorkbenchOption
    {
        get => _workbenchOption;
        set => _ = SetDefaultAsync(value, sprite: false);
    }

    public ModelOption? SpriteOption
    {
        get => _spriteOption;
        set => _ = SetDefaultAsync(value, sprite: true);
    }

    private async Task SetDefaultAsync(ModelOption? option, bool sprite)
    {
        if (_refreshing || option is null)
            return;
        try
        {
            if (sprite)
                await _catalog.SetSpriteModelAsync(option.Id);
            else
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
            if (ReferenceEquals(value, _selectedItem))
                return;
            _ = SelectAsync(value);
            OnPropertyChanged();
        }
    }

    public bool HasEditor => _editingId is not null;

    public bool ShowPlaceholder => !HasEditor;

    public bool IsNew => _isNew;

    // ---- 表單欄位 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
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
            var connectionNames = settings.Connections.ToDictionary(c => c.Id, c => c.Name);

            Connections.Clear();
            foreach (var c in settings.Connections)
                Connections.Add(new ConnectionOption(c.Id, c.Name));

            Items.Clear();
            foreach (var m in settings.Models
                         .OrderBy(m => connectionNames.GetValueOrDefault(m.ConnectionId), StringComparer.CurrentCulture)
                         .ThenBy(m => m.DisplayName, StringComparer.CurrentCulture))
            {
                Items.Add(new ModelListItem(m.Id, connectionNames.GetValueOrDefault(m.ConnectionId, ""), m.DisplayName, m.ModelId,
                    m.Id == settings.WorkbenchModelId, m.Id == settings.SpriteModelId));
            }

            DefaultOptions.Clear();
            foreach (var item in Items)
                DefaultOptions.Add(new ModelOption(item.Id, $"{item.DisplayName} · {item.ConnectionName}"));
            _workbenchOption = DefaultOptions.FirstOrDefault(o => o.Id == settings.WorkbenchModelId);
            _spriteOption = DefaultOptions.FirstOrDefault(o => o.Id == settings.SpriteModelId);
            OnPropertyChanged(nameof(WorkbenchOption));
            OnPropertyChanged(nameof(SpriteOption));

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
        if (IsDirty && !await _confirm.ConfirmAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
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
        if (IsDirty && !await _confirm.ConfirmAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
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

        // 第一個模型設定時，預設模型會由目錄自動指定
        var item = Items.FirstOrDefault(i => i.Id == id);
        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        StartEditing(id, isNew: false, model.ConnectionId, model.ModelId, model.DisplayName, model.MaxOutputTokens,
            model.ContextWindow, model.ShowThinking, model.ToolsEnabled, displayNameFollows: model.DisplayName == model.ModelId);
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
        if (!await _confirm.ConfirmAsync(message, Strings.Confirm_Delete))
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
