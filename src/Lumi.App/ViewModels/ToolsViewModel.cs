using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Tools;

namespace Lumi.App.ViewModels;

/// <summary>確認方式的下拉選項。<see cref="AlwaysAllow"/> 是選項的值（false＝每次詢問，true＝永遠允許）。</summary>
public sealed record ToolConfirmOption(bool AlwaysAllow, string Text)
{
    public static IReadOnlyList<ToolConfirmOption> All =>
    [
        new(false, Strings.Tools_ConfirmAsk),
        new(true, Strings.Tools_ConfirmAlways),
    ];
}

/// <summary>MCP 工具的權限等級下拉選項（M16 D3）。</summary>
public sealed record ToolPermissionOption(ToolPermission Value, string Text)
{
    public static IReadOnlyList<ToolPermissionOption> All =>
    [
        new(ToolPermission.ReadOnly, Strings.Tools_PermissionReadOnly),
        new(ToolPermission.Write, Strings.Tools_PermissionWrite),
        new(ToolPermission.Network, Strings.Tools_PermissionNetwork),
        new(ToolPermission.Destructive, Strings.Tools_PermissionDestructive),
    ];
}

/// <summary>確認方式的三種情況：唯讀不需確認；破壞性或不能永遠允許的只能每次詢問；Write／Network 可以選。</summary>
public enum ToolConfirmKind { NotNeeded, AlwaysAsk, Choosable }

/// <summary>
/// 設定頁「工具」的一列（M15 15D）。開關與確認方式的變更交給 <see cref="ToolsViewModel"/> 儲存，儲存後整頁重新建立，
/// 所以這裡不自己保存變更後的狀態。
/// </summary>
public sealed class ToolItemViewModel
{
    private readonly Func<ToolItemViewModel, bool, Task> _setEnabled;
    private readonly Func<ToolItemViewModel, bool, Task> _setAlwaysAllow;
    private readonly bool _isEnabled;
    private readonly bool _alwaysAllow;
    private readonly Func<ToolItemViewModel, ToolPermission, Task> _setPermission;
    private readonly ToolPermission _permission;

    internal ToolItemViewModel(ITool tool, bool isEnabled, bool alwaysAllow,
        Func<ToolItemViewModel, bool, Task> setEnabled, Func<ToolItemViewModel, bool, Task> setAlwaysAllow,
        Func<ToolItemViewModel, ToolPermission, Task> setPermission)
    {
        var descriptor = tool.Descriptor;
        Name = descriptor.Name;
        DisplayName = descriptor.DisplayName;
        Summary = descriptor.Summary;
        PermissionText = descriptor.Permission switch
        {
            ToolPermission.Write => Strings.Tools_PermissionWrite,
            ToolPermission.Network => Strings.Tools_PermissionNetwork,
            ToolPermission.Destructive => Strings.Tools_PermissionDestructive,
            _ => Strings.Tools_PermissionReadOnly,
        };
        IsDestructive = descriptor.Permission == ToolPermission.Destructive;
        ModeText = descriptor.Modes switch
        {
            ToolModes.Workbench => Strings.Tools_ModeWorkbenchOnly,
            ToolModes.Sprite => Strings.Tools_ModeSpriteOnly,
            _ => null,
        };
        CanToggle = descriptor.CanDisable;
        ConfirmKind = descriptor.Permission == ToolPermission.ReadOnly ? ToolConfirmKind.NotNeeded
            : IToolCatalog.CanAlwaysAllow(descriptor) ? ToolConfirmKind.Choosable
            : ToolConfirmKind.AlwaysAsk;
        ConfirmText = ConfirmKind switch
        {
            ToolConfirmKind.NotNeeded => Strings.Tools_ConfirmNotNeeded,
            ToolConfirmKind.AlwaysAsk => Strings.Tools_ConfirmAlwaysAsk,
            _ => null,
        };
        AlwaysAskTooltip = ConfirmKind != ToolConfirmKind.AlwaysAsk ? null
            : IsDestructive ? Strings.Tools_AlwaysAskDestructiveTooltip
            // memory_save 是目前唯一不能永遠允許的非破壞性工具（寫進記憶＝永久性的提示注入，M14 Q2）
            : Name == "memory_save" ? Strings.Tools_AlwaysAskMemoryTooltip
            : null;
        _isEnabled = isEnabled;
        _alwaysAllow = alwaysAllow;
        _setEnabled = setEnabled;
        _setAlwaysAllow = setAlwaysAllow;
        _setPermission = setPermission;
        _permission = descriptor.Permission;
        // MCP 工具才有權限等級與伺服器的提示（只是建議，不可信，M16 D3）
        IsMcp = tool is IMcpTool;
        HintText = tool is IMcpTool mcp ? McpHintText(mcp.Hints) : null;
    }

    /// <summary>MCP 工具（16G）：才顯示權限等級的下拉選單與伺服器的提示。</summary>
    public bool IsMcp { get; }

    /// <summary>伺服器標示的說明（「伺服器標示：唯讀」等）；沒有提示或不是 MCP 工具時是 null。</summary>
    public string? HintText { get; }

    private static string? McpHintText(McpToolHints? hints) =>
        hints?.Destructive == true ? Strings.Tools_McpHintDestructive
        : hints?.ReadOnly == true ? Strings.Tools_McpHintReadOnly
        : null;

    public string Name { get; }

    public string DisplayName { get; }

    public string? Summary { get; }

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    /// <summary>權限徽章的文字（唯讀、寫入、網路、破壞性）。</summary>
    public string PermissionText { get; }

    /// <summary>破壞性的徽章用 Danger 淡底。</summary>
    public bool IsDestructive { get; }

    /// <summary>模式文字（只限工作台、只限小精靈）；兩邊都能用時是 null。</summary>
    public string? ModeText { get; }

    /// <summary>false＝設定頁不能關（<c>show_details</c>）。</summary>
    public bool CanToggle { get; }

    /// <summary>不能關的原因；可以關時是 null。</summary>
    public string? LockedNote => CanToggle ? null : Strings.Tools_CannotDisable;

    public ToolConfirmKind ConfirmKind { get; }

    /// <summary>不能選的時候顯示的確認方式文字；可以選時是 null（改用下拉選單）。</summary>
    public string? ConfirmText { get; }

    /// <summary>「每次都會詢問」的 Tooltip（只說明原因，不是按鈕）。</summary>
    public string? AlwaysAskTooltip { get; }

    public bool CanChoose => ConfirmKind == ToolConfirmKind.Choosable;

    public IReadOnlyList<ToolConfirmOption> ConfirmOptions => ToolConfirmOption.All;

    /// <summary>權限等級的下拉選項（只有 MCP 工具顯示）。</summary>
    public IReadOnlyList<ToolPermissionOption> PermissionOptions => ToolPermissionOption.All;

    /// <summary>
    /// MCP 工具的權限等級（M16 D3）。改變時交給 <see cref="ToolsViewModel"/> 儲存（改成唯讀前會先確認）；
    /// 內建工具的等級固定，這裡不會改變。
    /// </summary>
    public ToolPermission SelectedPermission
    {
        get => _permission;
        set
        {
            if (!IsMcp || value == _permission)
                return;
            _ = _setPermission(this, value);
        }
    }

    /// <summary>勾選＝啟用。不能關的工具固定是 true。改變時立即儲存。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!CanToggle || value == _isEnabled)
                return;
            _ = _setEnabled(this, value);
        }
    }

    /// <summary>「永遠允許」：只有 <see cref="CanChoose"/> 時有意義。設成 true 時先確認，取消就不會改變。</summary>
    public bool AlwaysAllow
    {
        get => _alwaysAllow;
        set
        {
            if (!CanChoose || value == _alwaysAllow)
                return;
            _ = _setAlwaysAllow(this, value);
        }
    }
}

/// <summary>設定頁「工具」的一個群組（M15 15D）。三態勾選框：全部啟用＝勾、全部關閉＝空、部分＝不確定。</summary>
public sealed class ToolGroupViewModel
{
    private readonly Func<IReadOnlyList<string>, bool, Task> _setEnabled;
    private readonly bool _memoryEnabled;

    internal ToolGroupViewModel(ToolGroupInfo info, IReadOnlyList<ToolItemViewModel> items, bool memoryEnabled,
        Func<IReadOnlyList<string>, bool, Task> setEnabled, string? statusText, string? offlineNote)
    {
        Id = info.Id;
        Title = info.Name;
        Items = items;
        _memoryEnabled = memoryEnabled;
        _setEnabled = setEnabled;
        StatusText = statusText;
        OfflineNote = offlineNote;
    }

    public string Id { get; }

    public string Title { get; }

    public IReadOnlyList<ToolItemViewModel> Items { get; }

    /// <summary>MCP 群組的連線狀態（「已連線 · 3 個工具」等）；內建群組是 null。</summary>
    public string? StatusText { get; }

    /// <summary>MCP 群組沒有連線時的說明（離線顯示快取，或還沒有取得過）；連線中或內建群組是 null。</summary>
    public string? OfflineNote { get; }

    /// <summary>「已啟用 2／3」。</summary>
    public string CountText => string.Format(CultureInfo.CurrentCulture, Strings.Tools_GroupCount,
        Items.Count(i => i.IsEnabled).ToString(CultureInfo.InvariantCulture), Items.Count.ToString(CultureInfo.InvariantCulture));

    /// <summary>群組裡至少有一個能關的工具；都不能關時群組勾選框停用。</summary>
    public bool CanToggle => Items.Any(i => i.CanToggle);

    /// <summary>只有記憶群組、記憶在「記憶」頁關閉時有說明；其餘是 null。</summary>
    public string? MemoryOffNote => Id == ToolGroups.Memory && !_memoryEnabled ? Strings.Tools_MemoryOff : null;

    /// <summary>全部啟用＝true、全部關閉＝false、部分＝null（不確定）。只看能關的工具。</summary>
    public bool? IsAllEnabled
    {
        get
        {
            var toggleable = Items.Where(i => i.CanToggle).ToList();
            if (toggleable.Count == 0)
                return true;
            var enabled = toggleable.Count(i => i.IsEnabled);
            return enabled == toggleable.Count ? true : enabled == 0 ? false : null;
        }
        set
        {
            if (!CanToggle)
                return;
            // 依「點之前」的狀態決定：勾 → 全部關閉；空或不確定 → 全部啟用（控制項傳進來的值不可靠，三態的順序不一定一樣）
            var enableAll = IsAllEnabled != true;
            _ = _setEnabled(Items.Where(i => i.CanToggle).Select(i => i.Name).ToList(), enableAll);
        }
    }
}

/// <summary>
/// 設定頁「工具」的 ViewModel（M15 15D）：群組與工具清單、啟用與關閉、「永遠允許」、全部恢復預設，以及工作台預設模型關閉工具時的提醒。
/// 變更立即儲存（同記憶頁、小精靈頁）。儲存失敗時頁面頂端顯示原因，並把畫面重新載入成設定檔的值。
/// </summary>
public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly IToolCatalog _tools;
    private readonly IModelCatalog _catalog;
    private readonly IConfirmService _confirm;
    private readonly IMcpToolSource _mcp;

    /// <summary>同一時間只會有一個儲存在進行，後面的變更排隊，依序套用在最新的設定上。</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>MCP 群組的群組代號前綴（<c>mcp:{伺服器 Id}</c>，見 <see cref="IToolCatalog.ListGroups"/>）。</summary>
    private const string McpGroupPrefix = "mcp:";

    public ToolsViewModel(IToolCatalog tools, IModelCatalog settings, IConfirmService confirm, IMcpToolSource mcp, IUiDispatcher dispatcher)
    {
        _tools = tools;
        _catalog = settings;
        _confirm = confirm;
        _mcp = mcp;
        ResetToDefaultsCommand = new AsyncRelayCommand(ResetToDefaultsAsync, () => CanResetToDefaults);
        Rebuild();
        // 伺服器狀態或工具清單改變時重新建立（可能在背景執行緒，所以經 dispatcher 回到 UI 執行緒）
        mcp.Changed += (_, _) => dispatcher.Post(Rebuild);
    }

    /// <summary>群組清單（依群組順序；組內依名稱排序，由 <see cref="IToolCatalog.ListGroups"/> 決定）。</summary>
    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];

    public IAsyncRelayCommand ResetToDefaultsCommand { get; }

    /// <summary>儲存失敗時的訊息（頁面頂端）；沒有失敗時是 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    public bool HasSaveError => SaveError is not null;

    /// <summary>工作台預設模型關閉了「啟用工具」時的說明；否則 null。開啟頁面時讀一次，不訂閱變更。</summary>
    public string? ModelToolsOffWarning
    {
        get
        {
            var settings = _catalog.Current;
            var model = settings.Models.FirstOrDefault(m => m.Id == settings.WorkbenchModelId);
            return model is { ToolsEnabled: false }
                ? string.Format(CultureInfo.CurrentCulture, Strings.Tools_ModelToolsOff, model.DisplayName)
                : null;
        }
    }

    public bool HasModelToolsOffWarning => ModelToolsOffWarning is not null;

    /// <summary>目前不是預設（有關閉的工具或有永遠允許）時才能恢復預設。</summary>
    public bool CanResetToDefaults
    {
        get
        {
            // 集合要看內容（數量），不能比參考：ToolSettings 是 record，預設值的集合和設定檔讀出來的不是同一個物件
            var tools = _catalog.Current.Tools;
            return tools.Disabled.Count > 0 || tools.AlwaysAllow.Count > 0 || tools.PermissionOverrides.Count > 0;
        }
    }

    /// <summary>不能恢復預設時，旁邊寫的原因；能恢復時是 null。</summary>
    public string? ResetBlockedReason => CanResetToDefaults ? null : Strings.Tools_ResetBlocked;

    /// <summary>
    /// 從設定重新建立整頁。群組與工具的狀態只在這裡計算，所以儲存、失敗、取消確認之後都呼叫它，畫面就和設定檔一致。
    /// </summary>
    private void Rebuild()
    {
        var settings = _catalog.Current;
        var disabled = settings.Tools.Disabled.ToHashSet(StringComparer.Ordinal);
        var alwaysAllow = settings.Tools.AlwaysAllow.ToHashSet(StringComparer.Ordinal);

        var statuses = _mcp.GetStatuses().ToDictionary(s => s.ServerId, StringComparer.Ordinal);

        Groups.Clear();
        foreach (var group in _tools.ListGroups())
        {
            var items = group.Tools.Select(tool =>
            {
                var descriptor = tool.Descriptor;
                // 不能關的工具一律啟用，不管設定檔寫了什麼；MCP 工具沒有列在設定裡時也是啟用（M16 D6）
                var enabled = !descriptor.CanDisable || !disabled.Contains(descriptor.Name);
                return new ToolItemViewModel(tool, enabled, alwaysAllow.Contains(descriptor.Name),
                    (item, value) => SetEnabledAsync([item.Name], value),
                    SetAlwaysAllowAsync,
                    SetPermissionAsync);
            }).ToList();

            // MCP 群組：狀態文字與離線說明（M16 D12）。快取的工具仍照樣列出，只在說明寫清楚
            var serverId = McpServerIdOf(group.Group.Id);
            var status = serverId is null ? null : statuses.GetValueOrDefault(serverId);
            var statusText = serverId is null ? null : McpStatusText(status);
            var offlineNote = serverId is null || status?.State == McpServerState.Ready ? null
                : items.Count > 0 ? Strings.Tools_McpGroupOffline
                : Strings.Tools_McpGroupNoCache;

            Groups.Add(new ToolGroupViewModel(group.Group, items, settings.Memory.Enabled,
                (names, value) => SetEnabledAsync(names, value), statusText, offlineNote));
        }

        OnPropertyChanged(nameof(CanResetToDefaults));
        OnPropertyChanged(nameof(ResetBlockedReason));
        ResetToDefaultsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>MCP 群組的代號是 <c>mcp:{伺服器 Id}</c>；內建群組回 null。</summary>
    private static string? McpServerIdOf(string groupId) =>
        groupId.StartsWith(McpGroupPrefix, StringComparison.Ordinal) ? groupId[McpGroupPrefix.Length..] : null;

    private static string? McpStatusText(McpServerStatus? status)
    {
        if (status is null)
            return null;
        return status.State switch
        {
            McpServerState.Ready => string.Format(CultureInfo.CurrentCulture, Strings.Mcp_StatusReady,
                status.ToolCount.ToString(CultureInfo.InvariantCulture)),
            McpServerState.Connecting => Strings.Mcp_StatusConnecting,
            McpServerState.Failed => Strings.Mcp_StatusFailed,
            McpServerState.Disabled => Strings.Mcp_StatusDisabled,
            _ => null,
        };
    }

    /// <summary>
    /// MCP 工具的權限等級（M16 D3）。改成「唯讀」前先確認，取消就重新建立畫面；改成「破壞性」時一併移出「永遠允許」。
    /// 只寫這一個工具的覆寫，其餘（包括離線伺服器的）保持不變。
    /// </summary>
    private async Task SetPermissionAsync(ToolItemViewModel item, ToolPermission permission)
    {
        if (permission == ToolPermission.ReadOnly)
        {
            var confirmed = await _confirm.ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, Strings.Tools_McpReadOnlyConfirm, item.DisplayName), Strings.Tools_McpReadOnlyButton);
            if (!confirmed)
            {
                Rebuild();
                return;
            }
        }
        await ChangeAsync(tools => WithPermission(tools, item.Name, permission));
    }

    private static ToolSettings WithPermission(ToolSettings tools, string name, ToolPermission permission)
    {
        // 回到「寫入」（MCP 的預設）時移除覆寫，設定檔只記錄改過的工具
        var overrides = tools.PermissionOverrides
            .Where(p => p.Key != name)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (permission != ToolPermission.Write)
            overrides[name] = permission;

        // 破壞性的工具不能永遠允許，改成破壞性時一併移除
        var alwaysAllow = permission == ToolPermission.Destructive
            ? tools.AlwaysAllow.Where(n => n != name).ToList()
            : tools.AlwaysAllow.ToList();
        return tools with { PermissionOverrides = overrides, AlwaysAllow = alwaysAllow };
    }

    private Task SetEnabledAsync(IEnumerable<string> names, bool enabled) =>
        ChangeAsync(tools => WithEnabled(tools, names, enabled));

    private static ToolSettings WithEnabled(ToolSettings tools, IEnumerable<string> names, bool enabled)
    {
        var changed = names.ToHashSet(StringComparer.Ordinal);
        var disabled = enabled
            ? tools.Disabled.Where(n => !changed.Contains(n)).ToList()
            : tools.Disabled.Concat(changed).ToList();
        return tools with { Disabled = disabled };
    }

    /// <summary>「永遠允許」：開啟前先確認；取消就重新載入，選項會回到「每次詢問」。關閉不需確認。</summary>
    private async Task SetAlwaysAllowAsync(ToolItemViewModel item, bool alwaysAllow)
    {
        if (!alwaysAllow)
        {
            await ChangeAsync(tools => tools with { AlwaysAllow = tools.AlwaysAllow.Where(n => n != item.Name).ToList() });
            return;
        }

        var confirmed = await _confirm.ConfirmDestructiveAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Tools_AlwaysAllowConfirm, item.DisplayName), Strings.Tools_ConfirmAlways);
        if (!confirmed)
        {
            Rebuild();
            return;
        }
        await ChangeAsync(tools => tools with { AlwaysAllow = tools.AlwaysAllow.Append(item.Name).ToList() });
    }

    private async Task ResetToDefaultsAsync()
    {
        if (!CanResetToDefaults)
            return;
        if (!await _confirm.ConfirmAsync(Strings.Tools_ResetConfirm, Strings.Tools_ResetConfirmButton))
            return;
        await ChangeAsync(_ => ToolSettings.Default);
    }

    /// <summary>
    /// 在儲存閘門內，把變更套用在目前最新的設定上，寫檔後重新建立畫面。失敗時顯示原因，畫面仍會回到設定檔的值。
    /// </summary>
    private async Task ChangeAsync(Func<ToolSettings, ToolSettings> change)
    {
        await _saveGate.WaitAsync();
        try
        {
            SaveError = null;
            await _catalog.SetToolsAsync(change(_catalog.Current.Tools).Normalize());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SaveError = string.Format(CultureInfo.CurrentCulture, Strings.Tools_SaveFailed, ex.Message);
        }
        finally
        {
            _saveGate.Release();
        }
        Rebuild();
    }
}
