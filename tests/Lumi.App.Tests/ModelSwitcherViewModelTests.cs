using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

public sealed class ModelSwitcherViewModelTests
{
    private static async Task<(ModelSwitcherViewModel Switcher, SettingsHost Host, FakeSettingsWindowService Window, Func<bool> SetBlocked)> CreateAsync(
        Func<Task>? afterSelect = null)
    {
        var host = new SettingsHost();
        var ollama = new ConnectionSettings("ollama", "Ollama", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1");
        await host.Catalog.SaveConnectionAsync(ollama, null);
        await host.Catalog.SaveModelAsync(new ModelSettings("m-llama", "ollama", "llama3", "Llama 3", 4096, 128_000, false));
        await host.Catalog.SaveModelAsync(new ModelSettings("m-qwen", "ollama", "qwen3:8b", "Qwen 3", 4096, 32_000, false));
        var window = new FakeSettingsWindowService();
        var blocked = false;
        var switcher = new ModelSwitcherViewModel(host.Catalog, host.Status, window, new SyncUiDispatcher(), () => blocked, afterSelect);
        return (switcher, host, window, () => blocked = true);
    }

    [Fact]
    public async Task Entries_AreGroupedByConnection_WithHeaders_AndCurrentIsMarked()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;

        Assert.Equal(["Anthropic", "Claude Opus 5.5", "Ollama", "Llama 3", "Qwen 3"], switcher.Entries.Select(e => e.Text));
        Assert.Equal([true, false, true, false, false], switcher.Entries.Select(e => e.IsHeader));
        Assert.True(switcher.Entries.Single(e => e.Text == "Claude Opus 5.5").IsCurrent);
        Assert.Equal("claude-opus-5-5", switcher.Entries[1].Subtitle);
    }

    [Theory]
    [InlineData("qwen", new[] { "Ollama", "Qwen 3" })]           // 比對顯示名稱
    [InlineData("llama3", new[] { "Ollama", "Llama 3" })]        // 比對模型 ID
    [InlineData("anthropic", new[] { "Anthropic", "Claude Opus 5.5" })]   // 比對連線名稱
    [InlineData("ollama", new[] { "Ollama", "Llama 3", "Qwen 3" })]
    [InlineData("不存在", new string[0])]
    public async Task Search_FiltersModels_AndHidesEmptyGroups(string query, string[] expected)
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;

        switcher.SearchText = query;

        Assert.Equal(expected, switcher.Entries.Select(e => e.Text));
        Assert.Equal(expected.Length > 0, switcher.HasMatches);
    }

    [Fact]
    public async Task Select_ChangesWorkbenchDefaultModel_ClearsSearch_AndUpdatesCapsule()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;
        switcher.SearchText = "qwen";

        var ok = await switcher.SelectAsync(switcher.Entries.Single(e => e.Text == "Qwen 3"));

        Assert.True(ok);
        Assert.Equal("m-qwen", host.Catalog.Current.WorkbenchModelId);
        Assert.Equal("", switcher.SearchText);
        Assert.Equal("Qwen 3", switcher.CurrentName);
        Assert.Equal("Ollama", switcher.CurrentConnectionName);
        Assert.Equal("Qwen 3", switcher.CapsuleText);
    }

    [Fact]
    public async Task Select_RunsAfterSelectCallback()
    {
        var calls = 0;
        var (switcher, host, _, _) = await CreateAsync(() => { calls++; return Task.CompletedTask; });
        using var _ = host;

        await switcher.SelectAsync(switcher.Entries.Single(e => e.Text == "Llama 3"));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Select_WhileBlocked_DoesNothing()
    {
        var (switcher, host, _, block) = await CreateAsync();
        using var _ = host;
        block();

        Assert.False(await switcher.SelectAsync(switcher.Entries.Single(e => e.Text == "Llama 3")));

        Assert.NotEqual("m-llama", host.Catalog.Current.WorkbenchModelId);
    }

    [Fact]
    public async Task Select_OnHeader_Ignored()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;

        Assert.False(await switcher.SelectAsync(switcher.Entries[0]));
    }

    [Fact]
    public async Task Select_WhenCatalogIsReadOnly_ShowsError()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);
        // 唯讀時目錄是預設設定：有一個模型可選，但寫入會失敗
        var switcher = new ModelSwitcherViewModel(host.Catalog, host.Status, new FakeSettingsWindowService(), new SyncUiDispatcher(), () => false);
        var other = new ModelPickerEntry(host.Catalog.Current.Models[0].Id + "x", "x", "", false, false);

        Assert.False(await switcher.SelectAsync(other));

        Assert.True(switcher.HasError);
    }

    [Fact]
    public async Task CapsuleFollowsConnectionStatus()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;
        Assert.True(switcher.IsUnknown);

        host.Status.Mark("dev-anthropic", ConnectionStatus.Failed);
        Assert.True(switcher.IsFailed);

        host.Status.Mark("dev-anthropic", ConnectionStatus.Ok);
        Assert.True(switcher.IsOk);
    }

    [Fact]
    public async Task Manage_OpensSettingsAtModelsTab()
    {
        var (switcher, host, window, _) = await CreateAsync();
        using var _ = host;

        switcher.ManageCommand.Execute(null);

        Assert.Equal(SettingsSection.Models, Assert.Single(window.Shown).Section);
    }

    [Fact]
    public async Task NoModels_CapsuleSaysNotConfigured()
    {
        using var host = new SettingsHost();
        var switcher = new ModelSwitcherViewModel(host.Catalog, host.Status, new FakeSettingsWindowService(), new SyncUiDispatcher(), () => false);

        await host.Catalog.DeleteModelAsync(host.Catalog.Current.Models[0].Id);

        Assert.False(switcher.HasCurrent);
        Assert.Equal(Strings.Workbench_NoModelClickToSetup, switcher.CapsuleText);
        Assert.False(switcher.HasMatches);
    }

    [Fact]
    public async Task Select_RaisesSelectedEvent_SoTheFlyoutCanClose()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;
        var raised = 0;
        switcher.Selected += () => raised++;

        await switcher.SelectAsync(switcher.Entries.Single(e => e.Text == "Llama 3"));

        Assert.Equal(1, raised);
    }
}
