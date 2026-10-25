using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
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
    private readonly ToolsViewModel _vm;

    public ToolsViewModelTests()
    {
        _vm = new ToolsViewModel(StandardTools(), _catalog, _confirm);
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
        var vm = new ToolsViewModel(StandardTools(), _catalog, _confirm);

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
            _catalog, _confirm);

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
        var vm = new ToolsViewModel(StandardTools(), failing, _confirm);

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
        var vm = new ToolsViewModel(StandardTools(), _catalog, _confirm);

        Assert.True(vm.HasModelToolsOffWarning);
        Assert.Contains("測試模型", vm.ModelToolsOffWarning);
        Assert.Equal(string.Format(Strings.Tools_ModelToolsOff, "測試模型"), vm.ModelToolsOffWarning);
    }

    [Fact]
    public void Memory_off_note_only_appears_on_the_memory_group()
    {
        _catalog.Current = _catalog.Current with { Memory = new MemorySettings(false) };
        var vm = new ToolsViewModel(StandardTools(), _catalog, _confirm);

        Assert.Equal(Strings.Tools_MemoryOff, vm.Groups.Single(g => g.Id == ToolGroups.Memory).MemoryOffNote);
        Assert.Null(vm.Groups.Single(g => g.Id == ToolGroups.FilesWrite).MemoryOffNote);
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
        public Task SaveMcpServerAsync(Lumi.Core.Mcp.McpServerSettings server, Lumi.Core.Mcp.McpServerSecrets? newSecrets, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteMcpServerAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Lumi.Core.Mcp.McpServerSecrets> GetMcpSecretsAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public ResolvedModel? Resolve(string? modelSettingsId) => throw new NotSupportedException();
    }
}
