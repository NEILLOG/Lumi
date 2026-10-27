using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>
/// 「AI 連線」頁的「圖片生成」固定列的表單（M22 22G）：連線、模型 ID（可用模型清單可重新整理）、預設尺寸。
/// 存檔走 <see cref="IModelCatalog.SetImageGenerationAsync"/>；清單的載入與重新整理照 <see cref="ModelsViewModel"/> 的做法。
/// 同一時間只開一個編輯器，由 <see cref="AiSettingsViewModel"/> 協調。
/// </summary>
public sealed partial class ImageGenerationViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly IConnectionTester _tester;
    private readonly ConnectionStatusCache _status;
    private readonly IConfirmService _confirm;

    private ImageGenerationSettings _snapshot = ImageGenerationSettings.Default;   // 上次載入或存好的值：判斷有沒有未儲存的變更
    private bool _loading;
    private CancellationTokenSource? _listCts;

    public ImageGenerationViewModel(IModelCatalog catalog, IConnectionTester tester, ConnectionStatusCache status,
        IConfirmService confirm, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _tester = tester;
        _status = status;
        _confirm = confirm;
        SizeOptions =
        [
            new ImageSizeOption("1024x1024", Strings.ImageGen_SizeSquare),
            new ImageSizeOption("1536x1024", Strings.ImageGen_SizeLandscape),
            new ImageSizeOption("1024x1536", Strings.ImageGen_SizePortrait),
        ];
        Refresh();
        // 設定變動（別處儲存、連線被刪除）時重新整理連線清單與清單列的摘要；表單上的草稿不動
        catalog.Changed += (_, _) => dispatcher.Post(Refresh);
    }

    public ObservableCollection<ConnectionOption> Connections { get; } = [];

    public ObservableCollection<ImageSizeOption> SizeOptions { get; }

    public ObservableCollection<string> AvailableModelIds { get; } = [];

    /// <summary>有沒有開著編輯器（清單選到「圖片生成」才會開）。</summary>
    public bool HasEditor { get; private set; }

    /// <summary>清單列的摘要：「連線名稱 · 模型 ID」；沒設定（或連線已不存在）就是「尚未設定」。</summary>
    public string Summary
    {
        get
        {
            var settings = _catalog.Current.ImageGeneration;
            var connection = _catalog.Current.Connections.FirstOrDefault(c => c.Id == settings.ConnectionId);
            return settings.IsConfigured && connection is not null
                ? string.Format(CultureInfo.CurrentCulture, Strings.ImageGen_SummaryFormat, connection.Name, settings.ModelId)
                : Strings.ImageGen_NotConfigured;
        }
    }

    // ---- 表單欄位 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(HasFieldErrors))]
    private ConnectionOption? _selectedConnection;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave), nameof(ModelIdError), nameof(HasFieldErrors))]
    private string _modelId = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanSave))]
    private ImageSizeOption? _selectedSize;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public bool HasSaveError => SaveError is not null;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isSaving;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasListStatus))]
    private string? _listStatusText;

    public bool HasListStatus => ListStatusText is not null;

    /// <summary>有未儲存的變更（取消鈕與放棄確認用）。</summary>
    public bool IsDirty => HasEditor && CurrentSettings() != _snapshot;

    public string? ModelIdError => string.IsNullOrWhiteSpace(ModelId) ? Strings.Models_IdRequired : null;

    /// <summary>必填欄位有錯（沒有連線、模型 ID 空白）：動作列旁邊用文字說明原因。</summary>
    public bool HasFieldErrors => HasEditor && (SelectedConnection is null || ModelIdError is not null);

    public bool CanSave => HasEditor && SelectedConnection is not null && SelectedSize is not null && ModelIdError is null && !IsSaving;

    /// <summary>CanSave 一變，儲存按鈕的啟用狀態就要跟著變（命令不會自己追蹤 CanSave）。</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(CanSave))
        {
            SaveCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasFieldErrors));
        }
    }

    private ImageGenerationSettings CurrentSettings() =>
        new ImageGenerationSettings(SelectedConnection?.Id, ModelId, SelectedSize?.Size ?? ImageGenerationSettings.DefaultSize).Normalize();

    /// <summary>連線下拉改了：換成這個連線的可用模型清單（不改模型 ID，使用者可以直接輸入）。</summary>
    partial void OnSelectedConnectionChanged(ConnectionOption? value)
    {
        if (_loading || value is null)
            return;
        _ = LoadAvailableModelsAsync(forceRefresh: false);
    }

    // ---- 開啟、關閉、還原 ----

    /// <summary>開啟表單，載入目前的設定。呼叫端要先關掉其他編輯器（有未儲存的變更先詢問）。</summary>
    public async Task OpenAsync()
    {
        _snapshot = _catalog.Current.ImageGeneration.Normalize();
        HasEditor = true;
        LoadFields(_snapshot);
        await LoadAvailableModelsAsync(forceRefresh: false);
    }

    /// <summary>關閉表單（切到別的列時用）。有未儲存的變更先詢問，使用者取消回 false；原本就沒開回 true。</summary>
    public async Task<bool> CloseEditorAsync()
    {
        if (!HasEditor)
            return true;
        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.Confirm_DiscardChanges, Strings.Confirm_Discard))
            return false;

        _listCts?.Cancel();
        HasEditor = false;
        AvailableModelIds.Clear();
        ListStatusText = null;
        SaveError = null;
        NotifyEditorStateChanged();
        return true;
    }

    /// <summary>取消：欄位還原成上次存好的值（連線換了就重新載入可用模型清單）。</summary>
    [RelayCommand]
    private async Task RevertAsync()
    {
        var before = SelectedConnection?.Id;
        LoadFields(_snapshot);
        if (before != SelectedConnection?.Id)
            await LoadAvailableModelsAsync(forceRefresh: false);
    }

    private void LoadFields(ImageGenerationSettings settings)
    {
        _loading = true;
        try
        {
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == settings.ConnectionId);
            ModelId = settings.ModelId ?? "";
            SelectedSize = SizeOptions.FirstOrDefault(o => o.Size == settings.Size) ?? SizeOptions[0];
            SaveError = null;
        }
        finally
        {
            _loading = false;
        }
        NotifyEditorStateChanged();
    }

    private void NotifyEditorStateChanged()
    {
        OnPropertyChanged(nameof(HasEditor));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ModelIdError));
        OnPropertyChanged(nameof(HasFieldErrors));
    }

    // ---- 儲存 ----

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var settings = CurrentSettings();
        IsSaving = true;
        try
        {
            if (!await RunAsync(() => _catalog.SetImageGenerationAsync(settings)))
                return;
        }
        finally
        {
            IsSaving = false;
        }

        _snapshot = settings;
        NotifyEditorStateChanged();
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        SaveError = null;
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SaveError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = Strings.Settings_SaveFailed;
        }
        return false;
    }

    // ---- 可用模型清單 ----

    [RelayCommand]
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
                _listCts = null;
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

    // ---- 連線清單 ----

    private void Refresh()
    {
        // 重建連線清單時，下拉會把所選連線寫成 null：換回同一個連線（不觸發清單重新載入）
        var selectedId = SelectedConnection?.Id;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            Connections.Clear();
            foreach (var c in _catalog.Current.Connections)
                Connections.Add(new ConnectionOption(c.Id, c.Name));
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == selectedId);
        }
        finally
        {
            _loading = wasLoading;
        }
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>預設尺寸的一個選項：值是 API 的 size 字串，標籤是畫面上的說明。</summary>
public sealed record ImageSizeOption(string Size, string Label);
