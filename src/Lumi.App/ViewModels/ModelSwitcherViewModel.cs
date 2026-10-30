using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.ViewModels;

/// <param name="ModelSettingsId">標題列是 null。</param>
public sealed record ModelPickerEntry(string? ModelSettingsId, string Text, string Subtitle, bool IsHeader, bool IsCurrent)
{
    public bool IsModel => !IsHeader;
}

/// <summary>換模型的確認區（使用習慣提示第 1 條）：選到的模型、這次提示的紀錄 id、確認區的文字。</summary>
public sealed record PendingModelSwitch(ModelPickerEntry Target, string EntryId, string Text);

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
    private readonly Func<int>? _currentContextTokens;
    private readonly Func<Task>? _newConversation;
    private readonly UsageHintCoordinator? _hints;
    private readonly Func<string?>? _currentConversationId;

    /// <param name="currentContextTokens">目前這則對話的上下文大小（使用習慣提示第 1 條）；沒有提示功能時是 null。</param>
    /// <param name="newConversation">確認區的「開新對話再換」要開的新對話。</param>
    /// <param name="hints">提示的紀錄與判斷；null 時換模型不出現確認區，行為與原本相同。</param>
    /// <param name="currentConversationId">目前的對話 id（提示以對話為單位記錄）。</param>
    public ModelSwitcherViewModel(IModelCatalog catalog, ConnectionStatusCache status, ISettingsWindowService settingsWindow,
        IUiDispatcher dispatcher, Func<bool> isBlocked, Func<Task>? afterSelect = null,
        Func<int>? currentContextTokens = null, Func<Task>? newConversation = null,
        UsageHintCoordinator? hints = null, Func<string?>? currentConversationId = null)
    {
        _catalog = catalog;
        _status = status;
        _settingsWindow = settingsWindow;
        _isBlocked = isBlocked;
        _afterSelect = afterSelect;
        _currentContextTokens = currentContextTokens;
        _newConversation = newConversation;
        _hints = hints;
        _currentConversationId = currentConversationId;
        Rebuild();
        catalog.Changed += (_, _) => dispatcher.Post(Rebuild);
        status.Changed += (_, _) => dispatcher.Post(Rebuild);
    }

    public ObservableCollection<ModelPickerEntry> Entries { get; } = [];

    /// <summary>選了模型之後觸發：畫面據此關掉彈出選單。</summary>
    public event Action? Selected;

    /// <summary>換模型的確認區（第 1 條）：有值時確認區顯示在清單上方，選擇之前不換模型。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPendingSwitch))]
    private PendingModelSwitch? _pendingSwitch;

    public bool HasPendingSwitch => PendingSwitch is not null;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    // ---- 膠囊上顯示的目前模型 ----

    /// <summary>使用者自訂的顯示名稱（模型選單與設定清單的主文字）；膠囊不用它，膠囊顯示確切的 <see cref="ModelId"/>。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCurrent), nameof(CapsuleText), nameof(CapsuleTooltip))]
    private string _currentName = Strings.Workbench_NoModel;

    [ObservableProperty]
    private string _currentConnectionName = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsFailed), nameof(IsUnknown), nameof(StatusText), nameof(CapsuleTooltip))]
    private ConnectionStatus _currentStatus = ConnectionStatus.Unknown;

    /// <summary>供應商顯示名：用連線名稱（例如「Anthropic」「Ollama」）；連線沒有名稱時退回格式名稱。沒有模型時是空字串。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CapsuleText), nameof(CapsuleTooltip), nameof(ProviderInitial))]
    private string _providerName = "";

    /// <summary>確切的 model ID（<see cref="ModelSettings.ModelId"/>，例如 <c>gpt-5-mini</c>），不是自訂的顯示名稱。沒有模型時是空字串。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CapsuleText), nameof(CapsuleTooltip), nameof(CapsuleModelText))]
    private string _modelId = "";

    public bool HasCurrent => _catalog.Resolve(_catalog.Current.WorkbenchModelId) is not null;

    /// <summary>膠囊文字：「供應商 · model ID」；還沒有模型時是設定提示。</summary>
    public string CapsuleText => HasCurrent
        ? string.Format(Strings.Workbench_CapsuleFormat, ProviderName, ModelId)
        : Strings.Workbench_NoModelClickToSetup;

    /// <summary>供應商字首（供應商名稱的第一個字元，英文轉大寫）：Narrow 寬度的膠囊用它代替完整供應商名稱。沒有模型時是空字串。</summary>
    public string ProviderInitial
    {
        get
        {
            var name = ProviderName.Trim();
            return name.Length == 0 ? "" : System.Globalization.StringInfo.GetNextTextElement(name).ToUpperInvariant();
        }
    }

    /// <summary>Narrow 寬度的膠囊文字：只有確切的 model ID（供應商改由字首圖示與 Tooltip 表達）；還沒有模型時是設定提示。</summary>
    public string CapsuleModelText => HasCurrent ? ModelId : Strings.Workbench_NoModelClickToSetup;

    /// <summary>連線狀態的文字（已連線／連線失敗／尚未確認）；連線狀態不能只靠圓點。</summary>
    public string StatusText => CurrentStatus switch
    {
        ConnectionStatus.Ok => Strings.Workbench_StatusConnected,
        ConnectionStatus.Failed => Strings.Workbench_StatusFailed,
        _ => Strings.Workbench_StatusUnknown,
    };

    /// <summary>膠囊的 Tooltip：供應商與完整 model ID、連線狀態文字、費用歸屬。窄版文字被省略時靠它看完整名稱。</summary>
    public string CapsuleTooltip => HasCurrent
        ? string.Format(Strings.Workbench_CapsuleTooltipFormat, ProviderName, ModelId,
            string.Format(Strings.Workbench_ConnectionStatusFormat, StatusText), Strings.Workbench_CostNote)
        : Strings.Workbench_NoModelClickToSetup;

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
        ProviderName = current is null ? "" : ProviderNameOf(current.Connection);
        ModelId = current?.Model.ModelId ?? "";
        CurrentStatus = current is null ? ConnectionStatus.Unknown : _status.Get(current.Connection.Id).Status;
        OnPropertyChanged(nameof(HasCurrent));
        OnPropertyChanged(nameof(CapsuleText));
        OnPropertyChanged(nameof(CapsuleModelText));
        OnPropertyChanged(nameof(CapsuleTooltip));

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

    private static string ProviderNameOf(ConnectionSettings connection) =>
        !string.IsNullOrWhiteSpace(connection.Name)
            ? connection.Name.Trim()
            : connection.Format == ProviderFormat.Anthropic ? Strings.Format_Anthropic : Strings.Format_OpenAI;

    /// <summary>選了模型：改工作台預設模型；回應中不能切換。對話中途換模型且上下文夠大時，先出現確認區，不換（使用習慣提示第 1 條）。</summary>
    public async Task<bool> SelectAsync(ModelPickerEntry? entry)
    {
        if (entry?.ModelSettingsId is not { } id || _isBlocked())
            return false;

        Error = null;
        if (PendingSwitch is { } pending)
        {
            // 同一個模型：確認區照舊等使用者選；換成別的選項就把舊的確認區收起
            if (pending.Target.ModelSettingsId == id)
                return false;
            await DismissPendingSwitchAsync();
        }

        if (id != _catalog.Current.WorkbenchModelId && await HoldForModelSwitchHintAsync(entry))
            return false;
        return await SwitchToAsync(id);
    }

    /// <summary>確認區的「開新對話再換」：先開新對話，再換模型。</summary>
    [RelayCommand]
    private async Task SwitchInNewConversationAsync()
    {
        if (PendingSwitch is not { } pending)
            return;
        PendingSwitch = null;
        await RecordAsync(pending.EntryId, UsageHintOutcome.Acted);
        if (_newConversation is not null)
            await _newConversation();
        await SwitchToAsync(pending.Target.ModelSettingsId!);
    }

    /// <summary>確認區的「照樣換」。</summary>
    [RelayCommand]
    private async Task SwitchAnywayAsync()
    {
        if (PendingSwitch is not { } pending)
            return;
        PendingSwitch = null;
        await RecordAsync(pending.EntryId, UsageHintOutcome.Acted);
        await SwitchToAsync(pending.Target.ModelSettingsId!);
    }

    /// <summary>
    /// 確認區沒有被選擇就收起（關掉選單、送出新訊息、切換對話時）：記為 Dismissed，模型不換。沒有確認區時什麼都不做。
    /// </summary>
    public async Task DismissPendingSwitchAsync()
    {
        if (PendingSwitch is not { } pending)
            return;
        PendingSwitch = null;
        await RecordAsync(pending.EntryId, UsageHintOutcome.Dismissed);
    }

    /// <summary>上下文夠大、規則沒靜音、這則對話還沒提示過：設定確認區並回 true（呼叫端不換模型）。</summary>
    private async Task<bool> HoldForModelSwitchHintAsync(ModelPickerEntry entry)
    {
        if (_hints is null || _currentContextTokens is null || _currentConversationId?.Invoke() is not { } conversationId)
            return false;
        var prompt = await _hints.BeginModelSwitchAsync(conversationId, _currentContextTokens());
        if (prompt is null)
            return false;
        PendingSwitch = new PendingModelSwitch(entry, prompt.EntryId, prompt.Text);
        return true;
    }

    private Task RecordAsync(string entryId, UsageHintOutcome outcome) =>
        _hints?.ResolveModelSwitchAsync(entryId, outcome) ?? Task.CompletedTask;

    /// <summary>真正換工作台的預設模型，然後關掉選單（<see cref="Selected"/>）。</summary>
    private async Task<bool> SwitchToAsync(string id)
    {
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
