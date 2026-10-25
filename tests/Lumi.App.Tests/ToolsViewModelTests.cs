using System.Text.Json.Nodes;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Core.Tools;

namespace Lumi.App.Tests;

/// <summary>M15 15D：設定頁「工具」的 ViewModel——群組與三態勾選、啟用與關閉、「永遠允許」的確認、全部恢復預設、儲存失敗、模型與記憶的說明。</summary>
public sealed class ToolsViewModelTests
{
    private readonly FakeModelCatalog _catalog = new();
    private readonly FakeConfirmService _confirm = new();
    private readonly FakeMcpToolSource _mcpSource = new();
    private readonly ToolsViewModel _vm;

    public ToolsViewModelTests()
    {
        _vm = new ToolsViewModel(StandardTools(), _catalog, _confirm, _mcpSource, new SyncUiDispatcher());
    }

    /// <summary>一組覆蓋各種情況的工具：一般（唯讀）、寫入檔案（寫入＋破壞性）、筆記（寫入）、長期記憶（不能永遠允許）、小精靈（一個不能關）。</summary>
    private static FakeToolCatalog StandardTools() => new(
        FakeCatalogTool.Make("get_current_time", "查詢目前時間"),
        FakeCatalogTool.Make("write_file", "寫入檔案", ToolPermission.Write, ToolGroups.FilesWrite),
        FakeCatalogTool.Make("overwrite_file", "覆寫檔案", ToolPermission.Destructive, ToolGroups.FilesWrite),
        FakeCatalogTool.Make("add_note", "新增筆記", ToolPermission.Write, ToolGroups.Notes),
        FakeCatalogTool.Make("memory_save", "儲存記憶", ToolPermission.Write, ToolGroups.Memory, canAlwaysAllow: false),
        FakeCatalogTool.Make("show_details", "顯示詳細內容", group: ToolGroups.Sprite, canDisable: false, modes: ToolModes.Sprite),
        FakeCatalogTool.Make("sprite_note", "小精靈筆記", group: ToolGroups.Sprite, modes: ToolModes.Sprite));

    private ToolItemViewModel Item(string name) => _vm.Groups.SelectMany(g => g.Items).Single(i => i.Name == name);

    private ToolGroupViewModel Group(string id) => _vm.Groups.Single(g => g.Id == id);

    private ToolSettings Saved => _catalog.Current.Tools;

    [Fact]
    public void Groups_and_items_come_from_catalog_in_group_order_and_skip_empty_groups()
    {
        Assert.Equal(["一般", "寫入檔案", "筆記與對話", "長期記憶", "小精靈"], _vm.Groups.Select(g => g.Title).ToArray());
        Assert.Equal(["寫入檔案", "覆寫檔案"], Group("files-write").Items.Select(i => i.DisplayName).ToArray());
        Assert.Equal(["小精靈筆記", "顯示詳細內容"], Group("sprite").Items.Select(i => i.DisplayName).ToArray());
    }

    [Fact]
    public void Toggling_an_item_saves_the_disabled_list_and_updates_the_count()
    {
        Item("get_current_time").IsEnabled = false;

        Assert.Equal(["get_current_time"], Saved.Disabled);
        Assert.False(Item("get_current_time").IsEnabled);
        Assert.Equal("已啟用 0／1", Group("general").CountText);
        Assert.Equal("已啟用 2／2", Group("files-write").CountText);
    }

    [Fact]
    public void Group_checkbox_is_indeterminate_when_some_but_not_all_are_enabled()
    {
        Item("write_file").IsEnabled = false;

        Assert.Null(Group("files-write").IsAllEnabled);
        Assert.Equal("已啟用 1／2", Group("files-write").CountText);
    }

    [Fact]
    public void Group_toggle_from_partial_enables_all_and_from_all_enabled_disables_all()
    {
        Item("write_file").IsEnabled = false;
        Group("files-write").IsAllEnabled = true;   // 不確定 → 全部啟用
        Assert.Empty(Saved.Disabled);
        Assert.True(Group("files-write").IsAllEnabled);

        Group("files-write").IsAllEnabled = false;  // 勾 → 全部關閉
        Assert.Equal(["overwrite_file", "write_file"], Saved.Disabled);
        Assert.False(Group("files-write").IsAllEnabled);
    }

    [Fact]
    public void Group_toggle_skips_tools_that_cannot_be_disabled()
    {
        Group("sprite").IsAllEnabled = false;   // 全部關閉，但 show_details 不能關

        Assert.Equal(["sprite_note"], Saved.Disabled);
        Assert.True(Item("show_details").IsEnabled);
        Assert.True(Group("sprite").CanToggle);
    }

    [Fact]
    public void Locked_tool_stays_enabled_even_if_settings_list_it()
    {
        _catalog.Current = _catalog.Current with { Tools = new ToolSettings { Disabled = ["show_details"] } };
        var vm = new ToolsViewModel(StandardTools(), _catalog, _confirm, _mcpSource, new SyncUiDispatcher());

        var locked = vm.Groups.SelectMany(g => g.Items).Single(i => i.Name == "show_details");
        Assert.True(locked.IsEnabled);
        Assert.False(locked.CanToggle);
        Assert.NotNull(locked.LockedNote);
    }

    [Fact]
    public void Group_is_not_toggleable_when_no_tool_in_it_can_be_disabled()
    {
        var vm = new ToolsViewModel(new FakeToolCatalog(
            FakeCatalogTool.Make("show_details", "顯示詳細內容", group: ToolGroups.Sprite, canDisable: false, modes: ToolModes.Sprite)),
            _catalog, _confirm, _mcpSource, new SyncUiDispatcher());

        var group = vm.Groups.Single();
        group.IsAllEnabled = false;

        Assert.False(group.CanToggle);
        Assert.Empty(_catalog.ToolSaves);
    }

    [Fact]
    public void Always_allow_requires_confirmation_and_cancelling_keeps_it_off()
    {
        _confirm.Answer = false;

        Item("write_file").AlwaysAllow = true;

        Assert.Empty(_catalog.ToolSaves);
        Assert.False(Item("write_file").AlwaysAllow);
        var asked = Assert.Single(_confirm.Asked);
        Assert.Equal(Strings.Tools_ConfirmAlways, asked.ConfirmText);
        Assert.Contains("寫入檔案", asked.Message);
    }

    [Fact]
    public void Always_allow_saves_after_confirmation_and_turning_it_off_needs_no_confirmation()
    {
        Item("write_file").AlwaysAllow = true;

        Assert.Equal(["write_file"], Saved.AlwaysAllow);
        Assert.True(Item("write_file").AlwaysAllow);

        Item("write_file").AlwaysAllow = false;

        Assert.Empty(Saved.AlwaysAllow);
        Assert.Single(_confirm.Asked);
    }

    [Fact]
    public void Always_allow_is_only_offered_for_write_and_network_tools_that_allow_it()
    {
        Assert.Equal(ToolConfirmKind.NotNeeded, Item("get_current_time").ConfirmKind);
        Assert.Equal(Strings.Tools_ConfirmNotNeeded, Item("get_current_time").ConfirmText);
        Assert.Equal(ToolConfirmKind.Choosable, Item("write_file").ConfirmKind);
        Assert.True(Item("write_file").CanChoose);
        Assert.Null(Item("write_file").ConfirmText);

        Assert.Equal(ToolConfirmKind.AlwaysAsk, Item("overwrite_file").ConfirmKind);
        Assert.False(Item("overwrite_file").CanChoose);
        Assert.Equal(Strings.Tools_AlwaysAskDestructiveTooltip, Item("overwrite_file").AlwaysAskTooltip);
        Item("overwrite_file").AlwaysAllow = true;

        Assert.Equal(ToolConfirmKind.AlwaysAsk, Item("memory_save").ConfirmKind);
        Assert.Equal(Strings.Tools_AlwaysAskMemoryTooltip, Item("memory_save").AlwaysAskTooltip);
        Item("memory_save").AlwaysAllow = true;

        Assert.Empty(_confirm.Asked);
        Assert.Empty(_catalog.ToolSaves);
    }

    [Fact]
    public void Permission_and_mode_texts_describe_each_tool()
    {
        Assert.Equal("寫入", Item("write_file").PermissionText);
        Assert.Equal("破壞性", Item("overwrite_file").PermissionText);
        Assert.True(Item("overwrite_file").IsDestructive);
        Assert.Null(Item("write_file").ModeText);
        Assert.Equal(Strings.Tools_ModeSpriteOnly, Item("show_details").ModeText);
    }

    [Fact]
    public void Reset_restores_defaults_after_confirmation()
    {
        Item("get_current_time").IsEnabled = false;
        Item("write_file").AlwaysAllow = true;
        _confirm.Answer = false;
        _vm.ResetToDefaultsCommand.Execute(null);
        Assert.Equal(["get_current_time"], Saved.Disabled);   // 取消：不變

        _confirm.Answer = true;
        _vm.ResetToDefaultsCommand.Execute(null);

        Assert.Empty(Saved.Disabled);
        Assert.Empty(Saved.AlwaysAllow);
        Assert.True(Item("get_current_time").IsEnabled);
        Assert.False(_vm.CanResetToDefaults);
    }

    [Fact]
    public void Reset_is_disabled_when_already_default_and_says_why()
    {
        Assert.False(_vm.CanResetToDefaults);
        Assert.Equal(Strings.Tools_ResetBlocked, _vm.ResetBlockedReason);
        Assert.False(_vm.ResetToDefaultsCommand.CanExecute(null));

        _vm.ResetToDefaultsCommand.Execute(null);
        Assert.Empty(_confirm.Asked);
    }

    [Fact]
    public void Save_failure_shows_the_error_and_reloads_the_saved_state()
    {
        var failing = new SaveFailingCatalog();
        var vm = new ToolsViewModel(StandardTools(), failing, _confirm, _mcpSource, new SyncUiDispatcher());

        vm.Groups.SelectMany(g => g.Items).Single(i => i.Name == "get_current_time").IsEnabled = false;

        Assert.True(vm.HasSaveError);
        Assert.Equal(string.Format(Strings.Tools_SaveFailed, "磁碟已滿"), vm.SaveError);
        Assert.True(vm.Groups.SelectMany(g => g.Items).Single(i => i.Name == "get_current_time").IsEnabled);
    }

    [Fact]
    public void Model_tools_off_warning_shows_the_model_name_only_when_the_workbench_model_has_tools_off()
    {
        Assert.Null(_vm.ModelToolsOffWarning);
        Assert.False(_vm.HasModelToolsOffWarning);

        var off = FakeModelCatalog.Model with { ToolsEnabled = false };
        _catalog.Current = new AppSettings([FakeModelCatalog.Connection], [off], off.Id, off.Id, ThemePreference.System);
        var vm = new ToolsViewModel(StandardTools(), _catalog, _confirm, _mcpSource, new SyncUiDispatcher());

        Assert.True(vm.HasModelToolsOffWarning);
        Assert.Contains("測試模型", vm.ModelToolsOffWarning);
        Assert.Equal(string.Format(Strings.Tools_ModelToolsOff, "測試模型"), vm.ModelToolsOffWarning);
    }

    [Fact]
    public void Memory_off_note_only_appears_on_the_memory_group()
    {
        _catalog.Current = _catalog.Current with { Memory = new MemorySettings(false) };
        var vm = new ToolsViewModel(StandardTools(), _catalog, _confirm, _mcpSource, new SyncUiDispatcher());

        Assert.Equal(Strings.Tools_MemoryOff, vm.Groups.Single(g => g.Id == ToolGroups.Memory).MemoryOffNote);
        Assert.Null(vm.Groups.Single(g => g.Id == ToolGroups.FilesWrite).MemoryOffNote);
    }

    // ---- M16 16G：MCP 群組、權限等級 ----

    private const string McpServerId = "s1";
    private const string McpGroup = "mcp:s1";

    private static ToolItemViewModel ItemIn(ToolsViewModel vm, string name) => vm.Groups.SelectMany(g => g.Items).Single(i => i.Name == name);

    private ToolsViewModel McpViewModel(params ToolCatalogGroup[] mcpGroups) =>
        new(new McpCatalog(StandardTools(), mcpGroups), _catalog, _confirm, _mcpSource, new SyncUiDispatcher());

    private static ToolCatalogGroup McpServerGroup(string name, params ITool[] tools) =>
        new(new ToolGroupInfo(McpGroup, name, 100), tools);

    /// <summary>內建工具加上 MCP 群組；群組的排法和 <see cref="ToolCatalog"/> 相同（MCP 排在內建之後）。</summary>
    private sealed class McpCatalog(FakeToolCatalog builtIn, IReadOnlyList<ToolCatalogGroup> mcpGroups) : IToolCatalog
    {
        public Task<IReadOnlyList<ITool>> GetEnabledForNewConversationAsync(CancellationToken ct) => throw new NotSupportedException();

        public IReadOnlyList<ITool> GetAll() => builtIn.GetAll();

        public ITool? Find(string name) => throw new NotSupportedException();

        public IReadOnlyList<ToolCatalogGroup> ListGroups() => [.. builtIn.ListGroups(), .. mcpGroups];
    }

    /// <summary>MCP 工具：和 <c>McpTool</c> 一樣，權限讀設定裡的覆寫，沒有覆寫就是「寫入」。</summary>
    private sealed class FakeMcpTool(string name, string displayName, FakeModelCatalog settings, McpToolHints? hints = null) : IMcpTool
    {
        public ToolDescriptor Descriptor => new(name, displayName, "MCP 測試工具。",
            settings.Current.Tools.PermissionOverrides.TryGetValue(name, out var permission) ? permission : ToolPermission.Write,
            ToolModes.Workbench, TimeSpan.FromSeconds(60), Group: McpGroup, Summary: "MCP 說明。");

        public string ServerId => McpServerId;

        public bool IsLive => true;

        public McpToolHints Hints => hints ?? new McpToolHints(null, null);

        public JsonNode InputSchema { get; } = new JsonObject { ["type"] = "object" };

        public ToolPreview Preview(string argumentsJson, ToolContext context) => ToolPreview.None;

        public Task<ToolResult> ExecuteAsync(string argumentsJson, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("假結果"));
    }

    [Fact]
    public void Mcp_group_comes_after_built_in_groups_and_shows_the_server_status()
    {
        _mcpSource.Statuses.Add(new McpServerStatus(McpServerId, McpServerState.Ready, null, 2));
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool("mcp__s1__search", "搜尋", _catalog),
            new FakeMcpTool("mcp__s1__list", "列出", _catalog)));

        Assert.Equal(["一般", "寫入檔案", "筆記與對話", "長期記憶", "小精靈", "GitHub"], vm.Groups.Select(g => g.Title).ToArray());
        Assert.Equal("已連線 · 2 個工具", vm.Groups[^1].StatusText);
        Assert.Null(vm.Groups[^1].OfflineNote);
        Assert.Null(vm.Groups[0].StatusText);
        Assert.Null(vm.Groups[0].OfflineNote);
    }

    [Fact]
    public void Offline_server_keeps_cached_tools_with_a_note_and_a_server_without_cache_says_so()
    {
        _mcpSource.Statuses.Add(new McpServerStatus(McpServerId, McpServerState.Failed, "連線逾時", 0));
        var offline = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool("mcp__s1__search", "搜尋", _catalog)));

        Assert.Equal(Strings.Tools_McpGroupOffline, offline.Groups[^1].OfflineNote);
        Assert.Equal(Strings.Mcp_StatusFailed, offline.Groups[^1].StatusText);
        Assert.Single(offline.Groups[^1].Items);

        _mcpSource.Statuses.Clear();
        _mcpSource.Statuses.Add(new McpServerStatus(McpServerId, McpServerState.Connecting, null, 0));
        var empty = McpViewModel(McpServerGroup("Notion"));

        Assert.Equal(Strings.Tools_McpGroupNoCache, empty.Groups[^1].OfflineNote);
        Assert.Equal(Strings.Mcp_StatusConnecting, empty.Groups[^1].StatusText);
    }

    [Fact]
    public void Status_change_rebuilds_the_mcp_group()
    {
        _mcpSource.Statuses.Add(new McpServerStatus(McpServerId, McpServerState.Connecting, null, 0));
        var vm = McpViewModel(McpServerGroup("GitHub"));
        Assert.Equal(Strings.Mcp_StatusConnecting, vm.Groups[^1].StatusText);

        _mcpSource.Statuses.Clear();
        _mcpSource.Statuses.Add(new McpServerStatus(McpServerId, McpServerState.Ready, null, 0));
        _mcpSource.Raise();

        Assert.Equal(string.Format(Strings.Mcp_StatusReady, "0"), vm.Groups[^1].StatusText);
        Assert.Null(vm.Groups[^1].OfflineNote);
    }

    [Fact]
    public void Server_hints_are_advice_shown_only_for_mcp_tools()
    {
        var vm = McpViewModel(McpServerGroup("GitHub",
            new FakeMcpTool("mcp__s1__read", "讀取", _catalog, new McpToolHints(ReadOnly: true, Destructive: null)),
            new FakeMcpTool("mcp__s1__wipe", "清除", _catalog, new McpToolHints(ReadOnly: false, Destructive: true)),
            new FakeMcpTool("mcp__s1__plain", "一般", _catalog)));

        Assert.Equal(Strings.Tools_McpHintReadOnly, ItemIn(vm, "mcp__s1__read").HintText);
        Assert.Equal(Strings.Tools_McpHintDestructive, ItemIn(vm, "mcp__s1__wipe").HintText);
        Assert.Null(ItemIn(vm, "mcp__s1__plain").HintText);
        Assert.False(ItemIn(vm, "write_file").IsMcp);
        Assert.Null(ItemIn(vm, "write_file").HintText);
        Assert.True(ItemIn(vm, "mcp__s1__plain").IsMcp);
    }

    [Fact]
    public void Mcp_tool_is_enabled_by_default_and_its_permission_is_saved_as_an_override()
    {
        const string name = "mcp__s1__search";
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool(name, "搜尋", _catalog)));
        Assert.True(ItemIn(vm, name).IsEnabled);
        Assert.Equal(ToolPermission.Write, ItemIn(vm, name).SelectedPermission);

        ItemIn(vm, name).SelectedPermission = ToolPermission.Network;

        Assert.Equal(ToolPermission.Network, Saved.PermissionOverrides[name]);
        Assert.Equal(ToolPermission.Network, ItemIn(vm, name).SelectedPermission);
        Assert.Equal(ToolConfirmKind.Choosable, ItemIn(vm, name).ConfirmKind);

        ItemIn(vm, name).SelectedPermission = ToolPermission.Write;

        Assert.False(Saved.PermissionOverrides.ContainsKey(name));
    }

    [Fact]
    public void Making_an_mcp_tool_read_only_asks_first_and_cancelling_changes_nothing()
    {
        const string name = "mcp__s1__search";
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool(name, "搜尋", _catalog)));

        _confirm.Answer = false;
        ItemIn(vm, name).SelectedPermission = ToolPermission.ReadOnly;

        Assert.Empty(_catalog.ToolSaves);
        Assert.Equal(ToolPermission.Write, ItemIn(vm, name).SelectedPermission);
        var asked = Assert.Single(_confirm.Asked);
        Assert.Equal(Strings.Tools_McpReadOnlyButton, asked.ConfirmText);
        Assert.Equal(string.Format(Strings.Tools_McpReadOnlyConfirm, "搜尋"), asked.Message);

        _confirm.Answer = true;
        ItemIn(vm, name).SelectedPermission = ToolPermission.ReadOnly;

        Assert.Equal(ToolPermission.ReadOnly, Saved.PermissionOverrides[name]);
        Assert.Equal(ToolConfirmKind.NotNeeded, ItemIn(vm, name).ConfirmKind);
        Assert.Equal(2, _confirm.Asked.Count);
    }

    [Fact]
    public void Making_an_mcp_tool_destructive_removes_it_from_always_allow_without_asking()
    {
        const string name = "mcp__s1__search";
        _catalog.Current = _catalog.Current with { Tools = new ToolSettings { AlwaysAllow = [name] } };
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool(name, "搜尋", _catalog)));

        ItemIn(vm, name).SelectedPermission = ToolPermission.Destructive;

        Assert.Empty(Saved.AlwaysAllow);
        Assert.Equal(ToolPermission.Destructive, Saved.PermissionOverrides[name]);
        Assert.Equal(ToolConfirmKind.AlwaysAsk, ItemIn(vm, name).ConfirmKind);
        Assert.Empty(_confirm.Asked);
    }

    [Fact]
    public void Built_in_tools_have_no_permission_choice()
    {
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool("mcp__s1__search", "搜尋", _catalog)));

        ItemIn(vm, "write_file").SelectedPermission = ToolPermission.Network;

        Assert.Empty(_catalog.ToolSaves);
        Assert.Empty(Saved.PermissionOverrides);
    }

    [Fact]
    public void Saving_keeps_settings_for_tools_that_are_not_in_the_catalog()
    {
        const string name = "mcp__s1__search";
        _catalog.Current = _catalog.Current with
        {
            Tools = new ToolSettings
            {
                Disabled = ["mcp__gone__x"],
                PermissionOverrides = new Dictionary<string, ToolPermission> { ["mcp__gone__x"] = ToolPermission.Network },
            },
        };
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool(name, "搜尋", _catalog)));

        ItemIn(vm, name).IsEnabled = false;
        ItemIn(vm, name).SelectedPermission = ToolPermission.Destructive;

        Assert.Equal(["mcp__gone__x", name], Saved.Disabled);
        Assert.Equal(ToolPermission.Network, Saved.PermissionOverrides["mcp__gone__x"]);
        Assert.Equal(ToolPermission.Destructive, Saved.PermissionOverrides[name]);
    }

    [Fact]
    public void Permission_overrides_count_for_reset_and_reset_clears_them()
    {
        const string name = "mcp__s1__search";
        var vm = McpViewModel(McpServerGroup("GitHub", new FakeMcpTool(name, "搜尋", _catalog)));
        Assert.False(vm.CanResetToDefaults);

        ItemIn(vm, name).SelectedPermission = ToolPermission.Network;
        Assert.True(vm.CanResetToDefaults);

        vm.ResetToDefaultsCommand.Execute(null);

        Assert.Empty(Saved.PermissionOverrides);
        Assert.False(vm.CanResetToDefaults);
    }

    /// <summary>寫入工具設定時丟出 IOException 的目錄；讀取的成員回固定的設定。其餘成員這個測試用不到。</summary>
    private sealed class SaveFailingCatalog : IModelCatalog
    {
        public AppSettings Current { get; } = new([FakeModelCatalog.Connection], [FakeModelCatalog.Model],
            FakeModelCatalog.Model.Id, FakeModelCatalog.Model.Id, ThemePreference.System);

        public string? LoadProblem => null;

        public event EventHandler<AppSettings>? Changed { add { } remove { } }

        public Task SetToolsAsync(ToolSettings tools, CancellationToken ct = default) => throw new IOException("磁碟已滿");

        public Task SaveConnectionAsync(ConnectionSettings connection, SecretValue? newApiKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearApiKeyAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteConnectionAsync(string connectionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveModelAsync(ModelSettings model, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetWorkbenchModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSpriteModelAsync(string modelSettingsId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetThemeAsync(ThemePreference theme, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetThemeIdAsync(string themeId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetPersonaAsync(PersonaSettings persona, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSpriteAsync(SpriteSettings sprite, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetWorkbenchWindowAsync(WindowSettings window, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetSpeechAsync(SpeechSettings settings, SecretValue? newToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearSpeechTokenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetNotesFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetMemoryEnabledAsync(bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetNetworkAsync(Lumi.Core.Web.NetworkSettings settings, SecretValue? newProxyPassword, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> HasProxyPasswordAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveMcpServerAsync(Lumi.Core.Mcp.McpServerSettings server, Lumi.Core.Mcp.McpServerSecrets? newSecrets, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Lumi.Core.Mcp.McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public ResolvedModel? Resolve(string? modelSettingsId) => throw new NotSupportedException();
    }
}
