using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>settings.json 裡的 <c>behavior</c>：視窗置頂與更新檢查——預設值、舊檔相容、格式錯誤不影響其他設定、讀寫與變動通知。</summary>
public sealed class JsonModelCatalogBehaviorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-behavior-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogBehaviorTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NewFile_UsesTheDefaults()
    {
        var behavior = NewCatalog().Current.Behavior;

        Assert.Equal(BehaviorSettings.Default, behavior);
        Assert.False(behavior.WorkbenchTopmost);   // 工作台一般視窗
        Assert.True(behavior.SpriteTopmost);       // 小精靈本來就浮在桌面上
        Assert.True(behavior.CheckForUpdates);
        Assert.Equal("", behavior.SkippedVersion);
    }

    [Fact]
    public async Task TheSettings_AreSaved_ReloadedAndAnnounced()
    {
        var catalog = NewCatalog();
        AppSettings? announced = null;
        catalog.Changed += (_, s) => announced = s;

        await catalog.SetBehaviorAsync(new BehaviorSettings(true, false, false, "  1.2.0 "));

        Assert.Equal("1.2.0", announced?.Behavior.SkippedVersion);
        var reloaded = NewCatalog().Current.Behavior;
        Assert.True(reloaded.WorkbenchTopmost);
        Assert.False(reloaded.SpriteTopmost);
        Assert.False(reloaded.CheckForUpdates);
        Assert.Equal("1.2.0", reloaded.SkippedVersion);
    }

    [Fact]
    public async Task Saving_DoesNotChangeTheOtherSettings()
    {
        var catalog = NewCatalog();
        await catalog.SetMemoryEnabledAsync(false);
        await catalog.SetShowTokenUsageAsync(true);

        await catalog.SetBehaviorAsync(new BehaviorSettings(WorkbenchTopmost: true));

        var reloaded = NewCatalog().Current;
        Assert.False(reloaded.Memory.Enabled);
        Assert.True(reloaded.Display.ShowTokenUsage);
        Assert.True(reloaded.Behavior.WorkbenchTopmost);
    }

    [Fact]
    public void AnOldFile_WithoutTheField_UsesTheDefaults()
    {
        File.WriteAllText(SettingsPath, "{ \"version\": 1, \"connections\": [], \"models\": [], \"theme\": \"Dark\" }");

        var current = NewCatalog().Current;

        Assert.Equal(BehaviorSettings.Default, current.Behavior);
        Assert.Equal(ThemePreference.Dark, current.Theme);
    }

    [Fact]
    public void APartialField_FillsInTheDefaultsForTheMissingValues()
    {
        File.WriteAllText(SettingsPath, "{ \"version\": 1, \"connections\": [], \"models\": [], \"theme\": \"Dark\", \"behavior\": { \"workbenchTopmost\": true } }");

        var behavior = NewCatalog().Current.Behavior;

        Assert.True(behavior.WorkbenchTopmost);
        Assert.True(behavior.SpriteTopmost);
        Assert.True(behavior.CheckForUpdates);
    }

    [Theory]
    [InlineData("\"behavior\": \"oops\"")]
    [InlineData("\"behavior\": [1, 2]")]
    [InlineData("\"behavior\": { \"workbenchTopmost\": \"yes\" }")]
    public void MalformedBehavior_IsDefault_AndKeepsTheRest(string behavior)
    {
        File.WriteAllText(SettingsPath, "{ \"version\": 1, \"connections\": [], \"models\": [], \"theme\": \"Dark\", " + behavior + " }");

        var current = NewCatalog().Current;

        Assert.Equal(BehaviorSettings.Default, current.Behavior);
        Assert.Equal(ThemePreference.Dark, current.Theme);
    }
}
