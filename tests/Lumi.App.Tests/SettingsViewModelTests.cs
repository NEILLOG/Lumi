using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;

namespace Lumi.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Navigate_SwitchesCategoryAndTab()
    {
        using var host = new SettingsHost();
        var vm = host.Settings;

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.General));
        Assert.True(vm.ShowGeneral);
        Assert.False(vm.ShowConnectionsAndModels);

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Models));
        Assert.True(vm.ShowConnectionsAndModels);
        Assert.Equal(1, vm.TabIndex);

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Connections, "dev-anthropic", FocusApiKey: true, Notice: "提示"));
        Assert.Equal(0, vm.TabIndex);
        Assert.Equal("dev-anthropic", vm.Connections.SelectedItem!.Id);
        Assert.Equal("提示", vm.Connections.Notice);
    }

    [Fact]
    public async Task Close_WithUnsavedChanges_AsksFirst()
    {
        using var host = new SettingsHost();
        var vm = host.Settings;
        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Empty(host.Confirm.Asked);

        await host.Connections.SelectAsync(host.Connections.Items.Single());
        host.Connections.Name = "改到一半";
        Assert.True(vm.HasUnsavedChanges);

        host.Confirm.Answer = false;
        Assert.False(await vm.ConfirmCloseAsync());
        host.Confirm.Answer = true;
        Assert.True(await vm.ConfirmCloseAsync());
        Assert.Equal(2, host.Confirm.Asked.Count);
    }

    [Fact]
    public async Task Close_WithUnsavedModelChanges_AsksToo()
    {
        using var host = new SettingsHost();
        await host.Models.SelectAsync(host.Models.Items.Single());
        host.Models.DisplayName = "改到一半";

        Assert.True(host.Settings.HasUnsavedChanges);
    }

    [Fact]
    public void LoadProblem_IsExposed()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);

        Assert.True(host.Settings.HasLoadProblem);
        Assert.Contains("較新版本", host.Settings.LoadProblem);
    }
}
