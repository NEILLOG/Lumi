using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M15 15A：settings.json 裡的 <c>tools</c>——預設、舊檔相容、格式錯誤不影響其他設定、寫入後讀回一致。</summary>
public sealed class JsonModelCatalogToolsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-tools-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogToolsTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Missing_tools_section_uses_default()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "dark"
            }
            """);

        var catalog = NewCatalog();

        Assert.Equal(ToolSettings.Default, catalog.Current.Tools);
        Assert.Empty(catalog.Current.Tools.Disabled);
        Assert.Empty(catalog.Current.Tools.AlwaysAllow);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
        Assert.Null(catalog.LoadProblem);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"not an object\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"disabled\": 5}")]
    [InlineData("{\"disabled\": [1, 2]}")]
    [InlineData("{\"alwaysAllow\": \"add_note\"}")]
    public void Malformed_tools_section_uses_default(string tools)
    {
        File.WriteAllText(SettingsPath, $$"""
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "light",
              "themeId": "indigo",
              "tools": {{tools}}
            }
            """);

        var catalog = NewCatalog();

        Assert.Equal(ToolSettings.Default, catalog.Current.Tools);
        Assert.Equal(ThemePreference.Light, catalog.Current.Theme);
        Assert.Equal("indigo", catalog.Current.ThemeId);
        Assert.Null(catalog.LoadProblem);
        Assert.Empty(Directory.GetFiles(_directory, "settings.json.broken-*"));
    }

    [Fact]
    public async Task SetToolsAsync_round_trips_through_file()
    {
        var tools = new ToolSettings
        {
            Disabled = ["overwrite_file", "copy_file"],
            AlwaysAllow = ["add_note"],
        };
        await NewCatalog().SetToolsAsync(tools);

        var reloaded = NewCatalog();

        Assert.Equal(["copy_file", "overwrite_file"], reloaded.Current.Tools.Disabled);
        Assert.Equal(["add_note"], reloaded.Current.Tools.AlwaysAllow);
        Assert.Contains("\"tools\"", File.ReadAllText(SettingsPath));
        Assert.Contains("\"alwaysAllow\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task Saved_lists_are_sorted_and_distinct()
    {
        await NewCatalog().SetToolsAsync(new ToolSettings
        {
            Disabled = ["write_file", " ", "copy_file", "write_file", "", "add_note"],
            AlwaysAllow = ["move_file", "add_note", "move_file"],
        });

        var reloaded = NewCatalog();

        Assert.Equal(["add_note", "copy_file", "write_file"], reloaded.Current.Tools.Disabled);
        Assert.Equal(["add_note", "move_file"], reloaded.Current.Tools.AlwaysAllow);
        var json = File.ReadAllText(SettingsPath);
        Assert.Equal(1, CountOccurrences(json, "\"move_file\""));
    }

    [Fact]
    public async Task SetToolsAsync_KeepsOtherSettings()
    {
        var catalog = NewCatalog();
        await catalog.SetMemoryEnabledAsync(false);

        await catalog.SetToolsAsync(new ToolSettings { Disabled = ["copy_file"] });

        var reloaded = NewCatalog();
        Assert.False(reloaded.Current.Memory.Enabled);
        Assert.Equal(["copy_file"], reloaded.Current.Tools.Disabled);
        Assert.Null(reloaded.LoadProblem);
    }

    [Fact]
    public async Task MalformedTools_IsRewrittenAsDefault_OnTheNextSave()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "tools": "壞掉了"
            }
            """);

        await NewCatalog().SetMemoryEnabledAsync(false);

        var tools = NewCatalog().Current.Tools;
        Assert.Empty(tools.Disabled);   // 注意：Normalize 產生的是新的 List，不能拿來和 ToolSettings.Default 做 record 相等比較
        Assert.Empty(tools.AlwaysAllow);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
