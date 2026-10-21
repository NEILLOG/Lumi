using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>主題色票 ID 存在設定檔裡（UA）：選填欄位，版本號不變；舊檔沒有或不認得就是 sage。</summary>
public sealed class ThemeIdSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-theme-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ThemeId_SurvivesReload()
    {
        var catalog = NewCatalog();
        await catalog.SetThemeIdAsync("indigo");

        Assert.Equal("indigo", NewCatalog().Current.ThemeId);
    }

    [Fact]
    public async Task UnknownThemeId_IsStoredAsSage()
    {
        var catalog = NewCatalog();
        await catalog.SetThemeIdAsync("neon");

        Assert.Equal("sage", catalog.Current.ThemeId);
    }

    [Fact]
    public void OldFileWithoutThemeId_ReadsAsSage_AndKeepsVersion()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "version": 1, "connections": [], "models": [], "theme": "system" }""");

        var catalog = NewCatalog();

        Assert.Equal("sage", catalog.Current.ThemeId);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public void UnknownThemeIdInFile_ReadsAsSage()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "version": 1, "connections": [], "models": [], "theme": "system", "themeId": "neon" }""");

        Assert.Equal("sage", NewCatalog().Current.ThemeId);
    }
}
