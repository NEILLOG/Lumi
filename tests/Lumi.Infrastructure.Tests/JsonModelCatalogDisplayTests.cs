using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M20：settings.json 裡的 <c>display.showTokenUsage</c>——預設不顯示、舊檔相容、格式錯誤不影響其他設定、讀寫與變動通知。</summary>
public sealed class JsonModelCatalogDisplayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-display-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogDisplayTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NewFile_DoesNotShowTokenUsage()
    {
        Assert.Equal(DisplaySettings.Default, NewCatalog().Current.Display);
        Assert.False(NewCatalog().Current.Display.ShowTokenUsage);
    }

    [Fact]
    public async Task TheSwitch_IsSaved_ReloadedAndAnnounced()
    {
        var catalog = NewCatalog();
        AppSettings? announced = null;
        catalog.Changed += (_, s) => announced = s;

        await catalog.SetShowTokenUsageAsync(true);

        Assert.True(announced?.Display.ShowTokenUsage);
        Assert.True(NewCatalog().Current.Display.ShowTokenUsage);

        await catalog.SetShowTokenUsageAsync(false);
        Assert.False(NewCatalog().Current.Display.ShowTokenUsage);
    }

    [Fact]
    public async Task TurningItOn_DoesNotChangeTheOtherSettings()
    {
        var catalog = NewCatalog();
        await catalog.SetMemoryEnabledAsync(false);

        await catalog.SetShowTokenUsageAsync(true);

        var reloaded = NewCatalog().Current;
        Assert.False(reloaded.Memory.Enabled);
        Assert.True(reloaded.Display.ShowTokenUsage);
    }

    [Theory]
    [InlineData("\"display\": \"oops\"")]
    [InlineData("\"display\": [1, 2]")]
    [InlineData("\"display\": { \"showTokenUsage\": \"yes\" }")]
    public void MalformedDisplay_IsDefault_AndKeepsTheRest(string display)
    {
        File.WriteAllText(SettingsPath, "{ \"version\": 1, \"connections\": [], \"models\": [], \"theme\": \"Dark\", " + display + " }");

        var current = NewCatalog().Current;

        Assert.False(current.Display.ShowTokenUsage);
        Assert.Equal(ThemePreference.Dark, current.Theme);
    }
}
