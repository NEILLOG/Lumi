using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M9 9D：settings.json 裡的工作台視窗位置——讀寫、舊檔相容、格式錯誤只影響視窗位置。</summary>
public sealed class JsonModelCatalogWindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-window-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static WindowSettings Sample() => new(120, -40, 1100, 800, false, 2.0);

    [Fact]
    public void NewFile_HasNoWindowSettings()
    {
        var catalog = NewCatalog();

        Assert.Null(catalog.Current.WorkbenchWindow);
    }

    [Fact]
    public async Task WindowSettings_SurviveReload_WithNegativeCoordinates()
    {
        var catalog = NewCatalog();

        await catalog.SetWorkbenchWindowAsync(Sample());

        var reloaded = NewCatalog();
        Assert.Equal(Sample(), reloaded.Current.WorkbenchWindow);
        Assert.Null(reloaded.LoadProblem);
    }

    [Fact]
    public async Task SetWindow_KeepsOtherSettings()
    {
        var catalog = NewCatalog();
        var before = catalog.Current;

        await catalog.SetWorkbenchWindowAsync(new WindowSettings(0, 0, 1040, 760, true, 1.0));

        var reloaded = NewCatalog();
        Assert.Equal(before.Connections, reloaded.Current.Connections);
        Assert.Equal(before.Models, reloaded.Current.Models);
        Assert.Equal(before.Sprite, reloaded.Current.Sprite);
        Assert.Equal(before.ThemeId, reloaded.Current.ThemeId);
        Assert.True(reloaded.Current.WorkbenchWindow!.Maximized);
    }

    [Fact]
    public async Task SetWindow_DoesNotRaiseChanged()
    {
        var catalog = NewCatalog();
        var raised = 0;
        catalog.Changed += (_, _) => raised++;

        await catalog.SetWorkbenchWindowAsync(Sample());

        Assert.Equal(0, raised);
        Assert.Equal(Sample(), catalog.Current.WorkbenchWindow);
    }

    [Fact]
    public async Task SetWindow_WritesCamelCaseEntry_IntoTheSameFile()
    {
        var catalog = NewCatalog();

        await catalog.SetWorkbenchWindowAsync(Sample());

        var text = File.ReadAllText(SettingsPath);
        Assert.Contains("\"workbenchWindow\"", text);
        Assert.Contains("\"scaling\": 2", text);
        Assert.DoesNotContain(".tmp", Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public void OldFile_WithoutWindowField_LoadsWithDefaults()
    {
        Directory.CreateDirectory(_directory);
        // 9D 之前寫出的檔案：沒有 workbenchWindow，其他欄位照常讀
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [{ "id": "dev-anthropic", "name": "Anthropic", "format": "anthropic", "baseUrl": "https://api.anthropic.com" }],
              "models": [{ "id": "m1", "connectionId": "dev-anthropic", "modelId": "claude-opus-5-5", "displayName": "Opus", "maxOutputTokens": 64000, "contextWindow": 1000000, "showThinking": true }],
              "workbenchModelId": "m1",
              "spriteModelId": "m1",
              "theme": "dark"
            }
            """);

        var catalog = NewCatalog();

        Assert.Null(catalog.Current.WorkbenchWindow);
        Assert.Null(catalog.LoadProblem);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
        Assert.Single(catalog.Current.Models);
    }

    [Fact]
    public void MalformedWindowEntry_IsIgnored_WithoutResettingTheFile()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [{ "id": "dev-anthropic", "name": "Anthropic", "format": "anthropic", "baseUrl": "https://api.anthropic.com" }],
              "models": [],
              "theme": "dark",
              "workbenchWindow": { "x": "not-a-number", "y": 0, "width": 1040, "height": 760, "maximized": false, "scaling": 1 }
            }
            """);

        var catalog = NewCatalog();

        Assert.Null(catalog.Current.WorkbenchWindow);
        Assert.Null(catalog.LoadProblem);                                  // 不是損毀：不顯示「設定檔損毀」
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);         // 其他設定保留
        Assert.Single(catalog.Current.Connections);
        Assert.False(File.Exists(SettingsPath + ".broken"));
        Assert.Empty(Directory.GetFiles(_directory, "*.broken-*"));
    }

    [Fact]
    public void NullWindowEntry_IsTreatedAsMissing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "version": 1, "connections": [], "models": [], "workbenchWindow": null }""");

        var catalog = NewCatalog();

        Assert.Null(catalog.Current.WorkbenchWindow);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public void UnsafeValuesInFile_AreNormalizedOnLoad()
    {
        Directory.CreateDirectory(_directory);
        // 手動改檔：負寬度、超大高度、零縮放——讀進來時正規化，不會把不合理的值往下傳
        File.WriteAllText(SettingsPath, """
            { "version": 1, "connections": [], "models": [],
              "workbenchWindow": { "x": 5, "y": 6, "width": -10, "height": 1e9, "maximized": true, "scaling": 0 } }
            """);

        var window = NewCatalog().Current.WorkbenchWindow;

        Assert.NotNull(window);
        Assert.Equal(0, window!.Width);
        Assert.Equal(WindowSettings.MaxSize, window.Height);
        Assert.Equal(0, window.Scaling);
        Assert.True(window.Maximized);
    }

    [Fact]
    public async Task Writes_ReplaceOldValue()
    {
        var catalog = NewCatalog();

        await catalog.SetWorkbenchWindowAsync(Sample());
        await catalog.SetWorkbenchWindowAsync(new WindowSettings(10, 20, 900, 700, false, 1.0));

        Assert.Equal(new WindowSettings(10, 20, 900, 700, false, 1.0), NewCatalog().Current.WorkbenchWindow);
    }
}
