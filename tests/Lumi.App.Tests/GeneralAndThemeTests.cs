using Avalonia.Styling;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;

namespace Lumi.App.Tests;

public sealed class GeneralAndThemeTests
{
    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public async Task SelectingTheme_IsPersisted_AndSurvivesReload(ThemePreference preference)
    {
        using var host = new SettingsHost();

        await host.General.ApplyAsync(preference);

        Assert.Equal(preference, host.Catalog.Current.Theme);
        Assert.Equal(preference, host.General.SelectedTheme.Preference);
    }

    [Fact]
    public async Task SelectedThemeSetter_AppliesThroughCatalog()
    {
        using var host = new SettingsHost();

        host.General.SelectedTheme = host.General.Themes.Single(t => t.Preference == ThemePreference.Dark);
        await SettingsHost.WaitUntil(() => host.Catalog.Current.Theme == ThemePreference.Dark);

        Assert.Equal("深色", host.General.SelectedTheme.Text);
    }

    [Fact]
    public async Task ReadOnlyCatalog_ShowsError_AndRevertsSelection()
    {
        var newer = $$"""{ "version": {{Lumi.Infrastructure.Settings.JsonModelCatalog.CurrentVersion + 1}}, "connections": [], "models": [], "theme": "system" }""";
        using var host = new SettingsHost(newer);

        await host.General.ApplyAsync(ThemePreference.Dark);

        Assert.NotNull(host.General.Error);
        Assert.Equal(ThemePreference.System, host.General.SelectedTheme.Preference);
    }

    [Fact]
    public void ToVariant_MapsEveryPreference()
    {
        Assert.Equal(ThemeVariant.Default, ThemeService.ToVariant(ThemePreference.System));
        Assert.Equal(ThemeVariant.Light, ThemeService.ToVariant(ThemePreference.Light));
        Assert.Equal(ThemeVariant.Dark, ThemeService.ToVariant(ThemePreference.Dark));
    }

    [Fact]
    public async Task Attach_AppliesStoredThemeAtStartup_ThenFollowsChangesImmediately()
    {
        using var host = new SettingsHost();
        await host.Catalog.SetThemeAsync(ThemePreference.Light);
        var applied = new List<ThemeVariant>();

        new ThemeService(host.Catalog, new SyncUiDispatcher()).Attach(applied.Add);
        await host.Catalog.SetThemeAsync(ThemePreference.Dark);
        await host.Catalog.SetThemeAsync(ThemePreference.System);

        Assert.Equal([ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Default], applied);
    }
}
