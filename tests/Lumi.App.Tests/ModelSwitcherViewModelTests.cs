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
        // 膠囊顯示供應商與確切的 model ID，不是自訂的顯示名稱
        Assert.Equal("Ollama", switcher.ProviderName);
        Assert.Equal("qwen3:8b", switcher.ModelId);
        Assert.Equal("Ollama · qwen3:8b", switcher.CapsuleText);
    }

    [Fact]
    public async Task Capsule_ShowsProviderAndExactModelId_NotTheDisplayName()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;
        var current = host.Catalog.Resolve(host.Catalog.Current.WorkbenchModelId)!;

        Assert.Equal(current.Connection.Name, switcher.ProviderName);
        Assert.Equal(current.Model.ModelId, switcher.ModelId);
        Assert.NotEqual(current.Model.DisplayName, switcher.ModelId);
        Assert.Equal($"{current.Connection.Name} · {current.Model.ModelId}", switcher.CapsuleText);
        Assert.DoesNotContain(current.Model.DisplayName, switcher.CapsuleText);
    }

    [Theory]
    [InlineData(ProviderFormat.Anthropic)]
    [InlineData(ProviderFormat.OpenAICompatible)]
    public void ProviderName_FallsBackToTheFormatName_WhenTheConnectionNameIsBlank(ProviderFormat format)
    {
        var catalog = new FakeModelCatalog();
        var connection = new ConnectionSettings("c", "  ", format, "http://localhost:1/v1");
        var model = new ModelSettings("m", "c", "gpt-5-mini", "迷你", 4096, 128_000, false);
        catalog.Current = new AppSettings([connection], [model], model.Id, model.Id, ThemePreference.System);
        var status = new ConnectionStatusCache();

        var switcher = new ModelSwitcherViewModel(catalog, status, new FakeSettingsWindowService(), new SyncUiDispatcher(), () => false);

        var expected = format == ProviderFormat.Anthropic ? Strings.Format_Anthropic : Strings.Format_OpenAI;
        Assert.Equal(expected, switcher.ProviderName);
        Assert.Equal($"{expected} · gpt-5-mini", switcher.CapsuleText);
    }

    [Fact]
    public async Task CapsuleTooltip_HasProviderModelIdStatusText_AndTheCostNote_AndTracksTheStatus()
    {
        var (switcher, host, _, _) = await CreateAsync();
        using var _ = host;
        var changed = new List<string?>();
        switcher.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Contains(switcher.CapsuleText, switcher.CapsuleTooltip);
        Assert.Contains(Strings.Workbench_StatusUnknown, switcher.CapsuleTooltip);
        Assert.Contains(Strings.Workbench_CostNote, switcher.CapsuleTooltip);

        host.Status.Mark("dev-anthropic", ConnectionStatus.Ok);
        Assert.Contains(Strings.Workbench_StatusConnected, switcher.CapsuleTooltip);
        Assert.Equal(Strings.Workbench_StatusConnected, switcher.StatusText);
        Assert.Contains(nameof(ModelSwitcherViewModel.CapsuleTooltip), changed);

        host.Status.Mark("dev-anthropic", ConnectionStatus.Failed);
        Assert.Contains(Strings.Workbench_StatusFailed, switcher.CapsuleTooltip);
        Assert.DoesNotContain(Strings.Workbench_StatusConnected, switcher.CapsuleTooltip);
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
        Assert.Equal(Strings.Workbench_NoModelClickToSetup, switcher.CapsuleTooltip);
        Assert.Equal("", switcher.ProviderName);
        Assert.Equal("", switcher.ModelId);
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
