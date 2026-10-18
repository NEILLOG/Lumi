using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <param name="ModelSettingsId">標題列是 null。</param>
public sealed record ModelPickerEntry(string? ModelSettingsId, string Text, string Subtitle, bool IsHeader, bool IsCurrent)
{
    public bool IsModel => !IsHeader;
}

/// <summary>
/// 模型切換選單：依連線分組、可搜尋；選了模型＝改工作台預設模型（M2 的 Q3）。
/// 頂端的膠囊與錯誤列的「換模型重試」各用一個執行個體（後者選完會接著重送）。
/// </summary>
public partial class ModelSwitcherViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly ConnectionStatusCache _status;
    private readonly ISettingsWindowService _settingsWindow;
    private readonly Func<bool> _isBlocked;
    private readonly Func<Task>? _afterSelect;

    public ModelSwitcherViewModel(IModelCatalog catalog, ConnectionStatusCache status, ISettingsWindowService settingsWindow,
        IUiDispatcher dispatcher, Func<bool> isBlocked, Func<Task>? afterSelect = null)
    {
        _catalog = catalog;
        _status = status;
        _settingsWindow = settingsWindow;
        _isBlocked = isBlocked;
        _afterSelect = afterSelect;
        Rebuild();
        catalog.Changed += (_, _) => dispatcher.Post(Rebuild);
        status.Changed += (_, _) => dispatcher.Post(Rebuild);
    }

    public ObservableCollection<ModelPickerEntry> Entries { get; } = [];

    /// <summary>選了模型之後觸發：畫面據此關掉彈出選單。</summary>
    public event Action? Selected;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    // ---- 膠囊上顯示的目前模型 ----

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCurrent), nameof(CapsuleText))]
    private string _currentName = Strings.Workbench_NoModel;

    [ObservableProperty]
    private string _currentConnectionName = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsFailed), nameof(IsUnknown))]
    private ConnectionStatus _currentStatus = ConnectionStatus.Unknown;

    public bool HasCurrent => _catalog.Resolve(_catalog.Current.WorkbenchModelId) is not null;

    public string CapsuleText => HasCurrent ? CurrentName : Strings.Workbench_NoModelClickToSetup;

    public bool IsOk => CurrentStatus == ConnectionStatus.Ok;

    public bool IsFailed => CurrentStatus == ConnectionStatus.Failed;

    public bool IsUnknown => CurrentStatus == ConnectionStatus.Unknown;

    public bool HasMatches => Entries.Any(e => e.IsModel);

    partial void OnSearchTextChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var settings = _catalog.Current;
        var current = _catalog.Resolve(settings.WorkbenchModelId);
        CurrentName = current?.Model.DisplayName ?? Strings.Workbench_NoModel;
        CurrentConnectionName = current?.Connection.Name ?? "";
        CurrentStatus = current is null ? ConnectionStatus.Unknown : _status.Get(current.Connection.Id).Status;
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(CapsuleText));

        var query = SearchText.Trim();
        Entries.Clear();
        foreach (var connection in settings.Connections.OrderBy(c => c.Name, StringComparer.CurrentCulture))
        {
            var models = settings.Models
                .Where(m => m.ConnectionId == connection.Id)
                .Where(m => query.Length == 0
                    || m.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || m.ModelId.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || connection.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(m => m.DisplayName, StringComparer.CurrentCulture)
                .ToList();
            if (models.Count == 0)
                continue;

            Entries.Add(new ModelPickerEntry(null, connection.Name, "", IsHeader: true, IsCurrent: false));
            foreach (var model in models)
                Entries.Add(new ModelPickerEntry(model.Id, model.DisplayName, model.ModelId, IsHeader: false, model.Id == settings.WorkbenchModelId));
        }
        OnPropertyChanged(nameof(HasMatches));
    }

    /// <summary>選了模型：改工作台預設模型；回應中不能切換。</summary>
    public async Task<bool> SelectAsync(ModelPickerEntry? entry)
    {
        if (entry?.ModelSettingsId is not { } id || _isBlocked())
            return false;

        Error = null;
        try
        {
            if (id != _catalog.Current.WorkbenchModelId)
                await _catalog.SetWorkbenchModelAsync(id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex is IOException or UnauthorizedAccessException ? Strings.Settings_SaveFailed : ex.Message;
            return false;
        }

        SearchText = "";
        Selected?.Invoke();
        if (_afterSelect is not null)
            await _afterSelect();
        return true;
    }

    [RelayCommand]
    private void Manage() => _ = _settingsWindow.ShowAsync(new SettingsNavigation(SettingsSection.Models));

}
