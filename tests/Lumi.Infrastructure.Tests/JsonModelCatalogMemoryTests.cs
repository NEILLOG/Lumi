using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M14 14A：settings.json 裡的 <c>memory.enabled</c>——預設、舊檔相容、格式錯誤不影響其他設定、開關的讀寫與變動通知。</summary>
public sealed class JsonModelCatalogMemoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-memory-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogMemoryTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void NewFile_HasMemoryEnabledByDefault()
    {
        Assert.Equal(MemorySettings.Default, NewCatalog().Current.Memory);
        Assert.True(NewCatalog().Current.Memory.Enabled);
    }

    [Fact]
    public void OldFileWithoutMemory_LoadsWithDefault_AndKeepsTheRest()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "dark",
              "themeId": "teal"
            }
            """);

        var catalog = NewCatalog();

        Assert.True(catalog.Current.Memory.Enabled);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
        Assert.Equal("teal", catalog.Current.ThemeId);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public async Task SetMemoryEnabled_False_SurvivesReload()
    {
        await NewCatalog().SetMemoryEnabledAsync(false);

        Assert.False(NewCatalog().Current.Memory.Enabled);
    }

    [Fact]
    public async Task SetMemoryEnabled_BackToTrue_SurvivesReload()
    {
        var catalog = NewCatalog();
        await catalog.SetMemoryEnabledAsync(false);

        await catalog.SetMemoryEnabledAsync(true);

        Assert.True(NewCatalog().Current.Memory.Enabled);
    }

    [Fact]
    public async Task SetMemoryEnabled_WritesTheSectionToTheFile()
    {
        await NewCatalog().SetMemoryEnabledAsync(false);

        Assert.Contains("\"memory\"", File.ReadAllText(SettingsPath));
        Assert.Contains("\"enabled\": false", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task SetMemoryEnabled_RaisesChanged()
    {
        var catalog = NewCatalog();
        var seen = new List<bool>();
        catalog.Changed += (_, s) => seen.Add(s.Memory.Enabled);

        await catalog.SetMemoryEnabledAsync(false);
        await catalog.SetMemoryEnabledAsync(true);

        Assert.Equal([false, true], seen);
    }

    [Fact]
    public async Task SetMemoryEnabled_KeepsOtherSettings()
    {
        var catalog = NewCatalog();
        await catalog.SetWorkbenchWindowAsync(new WindowSettings(10, 20, 900, 700, false, 1.0));
        var connections = catalog.Current.Connections;

        await catalog.SetMemoryEnabledAsync(false);

        var reloaded = NewCatalog();
        Assert.Equal(new WindowSettings(10, 20, 900, 700, false, 1.0), reloaded.Current.WorkbenchWindow);
        Assert.Equal(connections.Count, reloaded.Current.Connections.Count);
        Assert.Null(reloaded.LoadProblem);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"not an object\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"enabled\": \"yes\"}")]
    [InlineData("{\"enabled\": null}")]
    [InlineData("{\"enabled\": 7}")]
    public void MalformedMemory_IsDefault_AndDoesNotRecoverTheFile(string memory)
    {
        File.WriteAllText(SettingsPath, $$"""
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "light",
              "themeId": "indigo",
              "memory": {{memory}}
            }
            """);

        var catalog = NewCatalog();

        Assert.Equal(MemorySettings.Default, catalog.Current.Memory);
        Assert.True(catalog.Current.Memory.Enabled);
        Assert.Equal(ThemePreference.Light, catalog.Current.Theme);
        Assert.Equal("indigo", catalog.Current.ThemeId);
        Assert.Null(catalog.LoadProblem);
        Assert.Empty(Directory.GetFiles(_directory, "settings.json.broken-*"));
    }

    [Fact]
    public void EmptyMemorySection_IsDefault()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "memory": {}
            }
            """);

        Assert.True(NewCatalog().Current.Memory.Enabled);
    }

    [Fact]
    public void ExplicitFalse_IsRead()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "memory": { "enabled": false }
            }
            """);

        Assert.False(NewCatalog().Current.Memory.Enabled);
    }

    [Fact]
    public async Task MalformedMemory_IsRewrittenAsDefault_OnTheNextSave()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "memory": "壞掉了"
            }
            """);
        var catalog = NewCatalog();

        await catalog.SetMemoryEnabledAsync(false);

        Assert.False(NewCatalog().Current.Memory.Enabled);
    }
}
