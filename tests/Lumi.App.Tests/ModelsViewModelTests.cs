using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

public sealed class ModelsViewModelTests
{
    /// <summary>加一個 OpenAI 相容連線（Ollama），回傳它的 Id。</summary>
    private static async Task<string> AddOllamaAsync(SettingsHost host)
    {
        var connection = new ConnectionSettings(ConnectionSettings.NewId(), "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");
        await host.Catalog.SaveConnectionAsync(connection, null);
        return connection.Id;
    }

    private static async Task<ModelsViewModel> NewEditorAsync(SettingsHost host, string? connectionId = null)
    {
        var vm = host.Models;
        await vm.NewModelCommand.ExecuteAsync(null);
        if (connectionId is not null)
            vm.SelectedConnection = vm.Connections.Single(c => c.Id == connectionId);
        return vm;
    }

    [Fact]
    public async Task List_IsOrderedByConnection_WithDefaultBadges()
    {
        using var host = new SettingsHost();
        var ollama = await AddOllamaAsync(host);
        var model = new ModelSettings(ModelSettings.NewId(), ollama, "llama3", "Llama 3", 4096, 128_000, false);
        await host.Catalog.SaveModelAsync(model);
        await host.Catalog.SetSpriteModelAsync(model.Id);   // 舊設定檔留下的小精靈模型：不再顯示

        var items = host.Models.Items;

        Assert.Equal(["Anthropic", "Ollama"], items.Select(i => i.ConnectionName));          // 依連線名稱排序
        Assert.Equal(Strings.Models_BadgeWorkbench, items[0].Badges);
        Assert.Equal("", items[1].Badges);
        Assert.Equal("Ollama · llama3", items[1].Subtitle);
        Assert.Equal(2, host.Models.DefaultOptions.Count);
    }

    [Fact]
    public async Task ChangingDefaultDropdowns_AppliesImmediately()
    {
        using var host = new SettingsHost();
        var ollama = await AddOllamaAsync(host);
        var model = new ModelSettings(ModelSettings.NewId(), ollama, "llama3", "Llama 3", 4096, 128_000, false);
        await host.Catalog.SaveModelAsync(model);
        var vm = host.Models;

        // 等「畫面的狀態」而不是目錄的值：目錄的值先變，Changed 事件（重建清單）稍後才跑，
        // 測試的執行緒和它同時動清單會互相干擾
        vm.WorkbenchOption = vm.DefaultOptions.Single(o => o.Id == model.Id);
        await SettingsHost.WaitUntil(() => vm.WorkbenchOption?.Id == model.Id && host.Catalog.Current.WorkbenchModelId == model.Id);

        Assert.Equal(model.Id, vm.WorkbenchOption!.Id);
        Assert.Equal(Strings.Models_BadgeWorkbench, vm.Items.Single(i => i.Id == model.Id).Badges);
    }

    [Fact]
    public async Task NewModel_DefaultsFollowConnectionFormat()
    {
        using var host = new SettingsHost();
        var ollama = await AddOllamaAsync(host);
        var vm = await NewEditorAsync(host);

        // 第一個連線是搬家建立的 Anthropic
        Assert.Equal(64_000, vm.MaxOutputTokens);
        Assert.Equal(1_000_000, vm.ContextWindow);
        Assert.True(vm.ShowThinking);

        vm.SelectedConnection = vm.Connections.Single(c => c.Id == ollama);

        Assert.Equal(4_096, vm.MaxOutputTokens);
        Assert.Equal(128_000, vm.ContextWindow);
        Assert.False(vm.ShowThinking);
    }

    [Fact]
    public async Task DisplayName_FollowsModelId_UntilUserEditsIt()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);

        vm.ModelId = "gpt-4o";
        Assert.Equal("gpt-4o", vm.DisplayName);
        vm.ModelId = "gpt-4o-mini";
        Assert.Equal("gpt-4o-mini", vm.DisplayName);

        vm.DisplayName = "我的 GPT";
        vm.ModelId = "gpt-5";
        Assert.Equal("我的 GPT", vm.DisplayName);
    }

    [Fact]
    public async Task Validation_BlocksSave()
    {
        using var host = new SettingsHost();
        var vm = await NewEditorAsync(host);

        Assert.NotNull(vm.ModelIdError);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.ModelId = "some-model";
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.MaxOutputTokens = 0;
        Assert.NotNull(vm.MaxOutputError);
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.MaxOutputTokens = 1000.5m;
        Assert.NotNull(vm.MaxOutputError);
        vm.MaxOutputTokens = 2048;
        vm.ContextWindow = null;
        Assert.NotNull(vm.ContextWindowError);
        vm.ContextWindow = 8000;
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.DisplayName = "  ";
        Assert.NotNull(vm.DisplayNameError);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_NewModel_AppearsAndPersists()
    {
        using var host = new SettingsHost();
        var ollama = await AddOllamaAsync(host);
        var vm = await NewEditorAsync(host, ollama);
        vm.ModelId = " llama3 ";
        vm.MaxOutputTokens = 2048;

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = host.Catalog.Current.Models.Single(m => m.ConnectionId == ollama);
        Assert.Equal("llama3", saved.ModelId);
        Assert.Equal("llama3", saved.DisplayName);
        Assert.Equal(2048, saved.MaxOutputTokens);
        Assert.Equal(128_000, saved.ContextWindow);
        Assert.False(vm.IsNew);
        Assert.Equal(saved.Id, vm.SelectedItem!.Id);
    }

    [Fact]
    public async Task EditExisting_ThenSave_PreservesToolsEnabled()
    {
        using var host = new SettingsHost();
        var vm = host.Models;
        var original = host.Catalog.Current.Models.Single();
        await host.Catalog.SaveModelAsync(original with { ToolsEnabled = false });
        await vm.SelectAsync(vm.Items.Single());
        Assert.False(vm.ToolsEnabled);
        vm.DisplayName = "改名字不該動到工具開關";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(Assert.Single(host.Catalog.Current.Models).ToolsEnabled);
    }

    [Fact]
    public async Task TogglingToolsEnabled_MakesTheEditorDirty_AndSaves()
    {
        using var host = new SettingsHost();
        var vm = host.Models;
        await vm.SelectAsync(vm.Items.Single());
        Assert.False(vm.IsDirty);

        vm.ToolsEnabled = false;

        Assert.True(vm.IsDirty);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(Assert.Single(host.Catalog.Current.Models).ToolsEnabled);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task NewModel_DefaultsToolsEnabled()
    {
        using var host = new SettingsHost();
        await host.Models.NewModelCommand.ExecuteAsync(null);

        Assert.True(host.Models.ToolsEnabled);
    }

    [Fact]
    public async Task EditExisting_ThenSave_UpdatesInPlace()
    {
        using var host = new SettingsHost();
        var vm = host.Models;
        await vm.SelectAsync(vm.Items.Single());
        vm.DisplayName = "我的 Opus";
        vm.ShowThinking = false;

        await vm.SaveCommand.ExecuteAsync(null);

        var model = Assert.Single(host.Catalog.Current.Models);
        Assert.Equal("我的 Opus", model.DisplayName);
        Assert.False(model.ShowThinking);
        Assert.Equal("claude-opus-5-5", model.ModelId);
    }

    [Fact]
    public async Task AvailableModels_AreListedFromConnection_AndFilledIntoDropdown()
    {
        using var host = new SettingsHost();
        host.Tester.Result = ConnectionTestResult.Ok(["claude-opus-5-5", "claude-sonnet-5-5"]);

        var vm = await NewEditorAsync(host);
        await SettingsHost.WaitUntil(() => vm.AvailableModelIds.Count == 2);

        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5"], vm.AvailableModelIds);
        Assert.Equal("可用模型 2 個，也可以直接輸入。", vm.ListStatusText);
        Assert.Null(host.Tester.Calls[0].Key);   // 用已儲存的金鑰，不是表單上的
    }

    [Fact]
    public async Task AvailableModels_WhenListFails_ExplainsAndAllowsManualEntry()
    {
        using var host = new SettingsHost();
        host.Tester.Result = FakeConnectionTester.Failure(ChatFailureKind.ModelNotFound, "找不到模型清單");

        var vm = await NewEditorAsync(host);
        await SettingsHost.WaitUntil(() => !vm.IsLoadingList && vm.ListStatusText is not null);

        Assert.Empty(vm.AvailableModelIds);
        Assert.Equal(Strings.Models_ListUnavailable, vm.ListStatusText);
        vm.ModelId = "手動輸入的模型";
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task AvailableModels_UseCachedTestResult_UntilRefreshIsPressed()
    {
        using var host = new SettingsHost();
        host.Status.Set("dev-anthropic", ConnectionTestResult.Ok(["cached-model"]));

        var vm = await NewEditorAsync(host);
        await SettingsHost.WaitUntil(() => vm.AvailableModelIds.Count == 1);
        Assert.Equal(["cached-model"], vm.AvailableModelIds);
        Assert.Empty(host.Tester.Calls);

        host.Tester.Result = ConnectionTestResult.Ok(["fresh-1", "fresh-2"]);
        await vm.RefreshModelListCommand.ExecuteAsync(null);

        Assert.Single(host.Tester.Calls);
        Assert.Equal(["fresh-1", "fresh-2"], vm.AvailableModelIds);
    }

    [Fact]
    public async Task Delete_OfDefaultModel_ReportsNewDefault()
    {
        using var host = new SettingsHost();
        var ollama = await AddOllamaAsync(host);
        var other = new ModelSettings(ModelSettings.NewId(), ollama, "llama3", "Llama 3", 4096, 128_000, false);
        await host.Catalog.SaveModelAsync(other);
        var vm = host.Models;
        await vm.SelectAsync(vm.Items.Single(i => i.ModelId == "claude-opus-5-5"));   // 目前的工作台預設

        host.Confirm.Answer = false;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, host.Catalog.Current.Models.Count);

        host.Confirm.Answer = true;
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Single(host.Catalog.Current.Models);
        Assert.Equal(other.Id, host.Catalog.Current.WorkbenchModelId);
        Assert.Equal("預設模型已改為「Llama 3」。", vm.DefaultChangedNotice);
        Assert.False(vm.HasEditor);
    }

    [Fact]
    public async Task Delete_OfLastModel_LeavesNoDefault()
    {
        using var host = new SettingsHost();
        var vm = host.Models;
        await vm.SelectAsync(vm.Items.Single());

        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Empty(host.Catalog.Current.Models);
        Assert.Null(host.Catalog.Current.WorkbenchModelId);
        Assert.Equal($"預設模型已改為「{Strings.Models_NoDefault}」。", vm.DefaultChangedNotice);
    }

    [Fact]
    public async Task WithoutConnections_CannotAddModel_AndHintIsShown()
    {
        using var host = new SettingsHost();
        await host.Catalog.DeleteConnectionAsync("dev-anthropic");

        Assert.False(host.Models.CanAddModel);
        Assert.True(host.Models.ShowNeedConnectionHint);
        Assert.False(host.Models.NewModelCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnsavedChanges_AskBeforeSwitching()
    {
        using var host = new SettingsHost();
        var ollama = await AddOllamaAsync(host);
        await host.Catalog.SaveModelAsync(new ModelSettings(ModelSettings.NewId(), ollama, "llama3", "Llama 3", 4096, 128_000, false));
        var vm = host.Models;
        var first = vm.Items[0];
        var second = vm.Items[1];

        await vm.SelectAsync(first);
        Assert.Empty(host.Confirm.Asked);

        vm.DisplayName = "改到一半";
        host.Confirm.Answer = false;
        Assert.False(await vm.SelectAsync(second));
        Assert.Equal("改到一半", vm.DisplayName);

        host.Confirm.Answer = true;
        Assert.True(await vm.SelectAsync(second));
        Assert.Equal("Llama 3", vm.DisplayName);
    }

    [Fact]
    public async Task ReadOnlyCatalog_ShowsError_AndRevertsDropdown()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);
        var vm = host.Models;

        var original = vm.WorkbenchOption;
        vm.WorkbenchOption = vm.DefaultOptions[0];   // 唯讀：儲存會失敗
        await SettingsHost.WaitUntil(() => vm.HasSaveError);

        Assert.Equal(original, vm.WorkbenchOption);
        Assert.Contains("較新版本", vm.SaveError);
    }
}
