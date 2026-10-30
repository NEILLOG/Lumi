using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;

namespace Lumi.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Navigate_SwitchesCategory_AndSelectsTheConnection()
    {
        using var host = new SettingsHost();
        var vm = host.Settings;

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.General));
        Assert.True(vm.ShowGeneral);
        Assert.False(vm.ShowConnectionsAndModels);
        Assert.False(vm.ShowPersona);

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Persona));
        Assert.True(vm.ShowPersona);
        Assert.False(vm.ShowGeneral);

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Models));
        Assert.True(vm.ShowConnectionsAndModels);

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Connections, "dev-anthropic", FocusApiKey: true, Notice: "提示"));
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

    [Fact]
    public async Task ReturnToChat_AsksTheWindowServiceToReturnToTheWorkbench()
    {
        using var host = new SettingsHost();
        Assert.Equal(0, host.WindowService.ReturnToWorkbenchCalls);

        await host.Settings.ReturnToChatCommand.ExecuteAsync(null);

        Assert.Equal(1, host.WindowService.ReturnToWorkbenchCalls);
        Assert.StartsWith("Lumi ", host.Settings.VersionText);
    }

    [Fact]
    public async Task Navigate_ReachesTheNewCategories()
    {
        using var host = new SettingsHost();
        var vm = host.Settings;

        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Reminders));
        Assert.True(vm.ShowReminders);
        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Privacy));
        Assert.True(vm.ShowPrivacy);
        Assert.False(vm.ShowReminders);
        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Memory));
        Assert.True(vm.ShowMemory);
        await vm.NavigateAsync(new SettingsNavigation(SettingsSection.Home));
        Assert.True(vm.ShowHome);
        Assert.Equal((int)SettingsCategory.Home, vm.CategoryIndex);
    }

    [Fact]
    public void TheCategoryNumbers_FollowTheMenuOrder()
    {
        Assert.Equal(
            ["Home", "Connections", "Usage", "Persona", "Memory", "Reminders", "Tools", "General", "Sprite", "Speech", "Network", "Privacy"],
            Enum.GetValues<SettingsCategory>().OrderBy(c => (int)c).Select(c => c.ToString()).ToArray());
    }

    [Fact]
    public async Task GoToCommand_NavigatesByTheSectionName_AndIgnoresUnknownNames()
    {
        using var host = new SettingsHost();
        var vm = host.Settings;

        await vm.GoToCommand.ExecuteAsync("Reminders");
        Assert.True(vm.ShowReminders);
        await vm.GoToCommand.ExecuteAsync("Models");
        Assert.True(vm.ShowConnectionsAndModels);
        await vm.GoToCommand.ExecuteAsync("Nope");
        Assert.True(vm.ShowConnectionsAndModels);
    }

    [Fact]
    public void ThePrivacyLinks_NavigateThroughTheSettingsViewModel()
    {
        using var host = new SettingsHost();

        host.Privacy.GoToCommand.Execute("Memory");

        Assert.True(host.Settings.ShowMemory);
    }
}
