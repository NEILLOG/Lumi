using Lumi.Core.Models;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>settings.json 裡的 <c>skills</c>（M29 D1）：預設、舊檔相容、讀寫、改名與格式錯誤時的退回。</summary>
public sealed class JsonModelCatalogSkillsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-skills-settings-" + Guid.NewGuid().ToString("N"));

    public JsonModelCatalogSkillsTests() => Directory.CreateDirectory(_directory);

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void OldFileWithoutSkills_UsesTheDefault_AndKeepsTheRest()
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

        Assert.Empty(catalog.Current.Skills.Disabled);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public async Task SetSkillEnabled_False_IsSaved_AndReloaded()
    {
        await NewCatalog().SetSkillEnabledAsync("週報", false);

        var reloaded = NewCatalog();
        Assert.True(reloaded.Current.Skills.IsDisabled("週報"));
        Assert.Contains("\"skills\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task SetSkillEnabled_True_RemovesIt_AndAnnounces()
    {
        var catalog = NewCatalog();
        await catalog.SetSkillEnabledAsync("週報", false);
        AppSettings? announced = null;
        catalog.Changed += (_, s) => announced = s;

        await catalog.SetSkillEnabledAsync("週報", true);

        Assert.NotNull(announced);
        Assert.False(announced.Skills.IsDisabled("週報"));
        Assert.Empty(NewCatalog().Current.Skills.Disabled);
    }

    [Fact]
    public async Task RenameSkill_MovesTheDisabledState()
    {
        var catalog = NewCatalog();
        await catalog.SetSkillEnabledAsync("週報", false);

        await catalog.RenameSkillAsync("週報", "週報2");

        var reloaded = NewCatalog();
        Assert.False(reloaded.Current.Skills.IsDisabled("週報"));
        Assert.True(reloaded.Current.Skills.IsDisabled("週報2"));
    }

    [Fact]
    public async Task RenameSkill_WhenEnabled_LeavesNothingDisabled()
    {
        var catalog = NewCatalog();

        await catalog.RenameSkillAsync("週報", "週報2");

        Assert.Empty(NewCatalog().Current.Skills.Disabled);
    }

    [Fact]
    public void MalformedSkillsValue_UsesTheDefault_AndKeepsOtherSettings()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "dark",
              "skills": "壞掉了"
            }
            """);

        var catalog = NewCatalog();

        Assert.Empty(catalog.Current.Skills.Disabled);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
        Assert.Null(catalog.LoadProblem);
    }

    [Fact]
    public void MalformedDisabledList_UsesTheDefault()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "skills": { "disabled": 5 }
            }
            """);

        Assert.Empty(NewCatalog().Current.Skills.Disabled);
    }

    [Fact]
    public async Task DisabledList_IsNormalizedOnSave()
    {
        var catalog = NewCatalog();
        await catalog.SetSkillEnabledAsync("b", false);
        await catalog.SetSkillEnabledAsync("A", false);
        await catalog.SetSkillEnabledAsync("a", false);

        Assert.Equal(["a", "b"], NewCatalog().Current.Skills.Disabled);   // 同名不分大小寫只留一筆，保留最後寫入的寫法
    }
}
