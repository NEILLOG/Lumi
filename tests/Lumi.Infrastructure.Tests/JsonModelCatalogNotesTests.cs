using Lumi.Core.Models;
using Lumi.Core.Notes;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>M10 10B：settings.json 裡的 <c>notes.folder</c>——讀寫、舊檔相容、格式錯誤不影響其他設定，以及筆記資料夾的即時解析。</summary>
public sealed class JsonModelCatalogNotesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-notes-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _variable = "LUMI_NOTES_SETTINGS_TEST_" + Guid.NewGuid().ToString("N");

    public JsonModelCatalogNotesTests()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable(_variable, Path.Combine(_directory, "vars"));
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), new InMemorySecretStore(), NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Abs(string name) => Path.Combine(_directory, name);

    [Fact]
    public void NewFile_HasDefaultFolder()
    {
        Assert.Equal(NotesSettings.Default, NewCatalog().Current.Notes);
        Assert.Equal("", NewCatalog().Current.Notes.Folder);
    }

    [Fact]
    public async Task SetFolder_SurvivesReload()
    {
        await NewCatalog().SetNotesFolderAsync(Abs("my notes"));

        Assert.Equal(Abs("my notes"), NewCatalog().Current.Notes.Folder);
    }

    [Fact]
    public async Task SetFolder_StoresTheRawValue_WithEnvironmentVariables()
    {
        // 存的是原文（含變數），解析是讀取時才做，所以之後變數改了也跟著變
        var catalog = NewCatalog();
        await catalog.SetNotesFolderAsync($"  %{_variable}%/notes  ");

        Assert.Equal($"%{_variable}%/notes", catalog.Current.Notes.Folder);
        Assert.Contains($"\"folder\": \"%{_variable}%/notes\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task SetFolder_Empty_ResetsToDefault()
    {
        var catalog = NewCatalog();
        await catalog.SetNotesFolderAsync(Abs("custom"));

        await catalog.SetNotesFolderAsync("");

        Assert.Equal("", NewCatalog().Current.Notes.Folder);
    }

    [Fact]
    public async Task SetFolder_RaisesChanged()
    {
        var catalog = NewCatalog();
        var seen = new List<string>();
        catalog.Changed += (_, s) => seen.Add(s.Notes.Folder);

        await catalog.SetNotesFolderAsync(Abs("x"));

        Assert.Equal([Abs("x")], seen);
    }

    [Theory]
    [InlineData("relative/notes")]
    [InlineData("$LUMI_NOTES_SETTINGS_TEST_NOT_DEFINED_xyz")]
    public async Task SetFolder_Invalid_ThrowsAndKeepsTheOldValue(string folder)
    {
        var catalog = NewCatalog();
        await catalog.SetNotesFolderAsync(Abs("kept"));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => catalog.SetNotesFolderAsync(folder));

        Assert.Contains("不合法", error.Message);
        Assert.Equal(Abs("kept"), NewCatalog().Current.Notes.Folder);
    }

    [Fact]
    public async Task SetFolder_KeepsOtherSettings()
    {
        var catalog = NewCatalog();
        await catalog.SetWorkbenchWindowAsync(new WindowSettings(10, 20, 900, 700, false, 1.0));
        var connections = catalog.Current.Connections;

        await catalog.SetNotesFolderAsync(Abs("n"));

        var reloaded = NewCatalog();
        Assert.Equal(new WindowSettings(10, 20, 900, 700, false, 1.0), reloaded.Current.WorkbenchWindow);
        Assert.Equal(connections.Count, reloaded.Current.Connections.Count);
        Assert.Null(reloaded.LoadProblem);
    }

    [Fact]
    public void OldFileWithoutNotes_LoadsWithDefault_AndKeepsTheRest()
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

        Assert.Equal(NotesSettings.Default, catalog.Current.Notes);
        Assert.Equal(ThemePreference.Dark, catalog.Current.Theme);
        Assert.Equal("teal", catalog.Current.ThemeId);
        Assert.Null(catalog.LoadProblem);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"not an object\"")]
    [InlineData("[1,2]")]
    [InlineData("{\"folder\": 7}")]
    public void MalformedNotes_IsDefault_AndDoesNotRecoverTheFile(string notes)
    {
        File.WriteAllText(SettingsPath, $$"""
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "light",
              "notes": {{notes}}
            }
            """);

        var catalog = NewCatalog();

        Assert.Equal(NotesSettings.Default, catalog.Current.Notes);
        Assert.Equal(ThemePreference.Light, catalog.Current.Theme);
        Assert.Null(catalog.LoadProblem);
        Assert.Empty(Directory.GetFiles(_directory, "settings.json.broken-*"));
    }

    [Fact]
    public void InvalidFolderInFile_IsKeptAsWritten_ForTheProviderToFallBackOn()
    {
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "connections": [],
              "models": [],
              "theme": "system",
              "notes": { "folder": "relative/path" }
            }
            """);

        Assert.Equal("relative/path", NewCatalog().Current.Notes.Folder);
    }

    // ---- 筆記資料夾的解析（讀取時即時做） ----

    [Fact]
    public async Task Provider_FollowsTheSettings_Immediately()
    {
        var catalog = NewCatalog();
        var provider = new SettingsNoteFolderProvider(catalog, NullLogger<SettingsNoteFolderProvider>.Instance);

        Assert.Equal(NoteFolderRules.DefaultFolder, provider.Folder);

        await catalog.SetNotesFolderAsync(Abs("moved"));
        Assert.Equal(Abs("moved"), provider.Folder);

        await catalog.SetNotesFolderAsync("");
        Assert.Equal(NoteFolderRules.DefaultFolder, provider.Folder);
    }

    [Fact]
    public async Task Provider_ExpandsEnvironmentVariables()
    {
        var catalog = NewCatalog();
        await catalog.SetNotesFolderAsync($"${_variable}/notes");
        var provider = new SettingsNoteFolderProvider(catalog, NullLogger<SettingsNoteFolderProvider>.Instance);

        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "vars", "notes")), provider.Folder);
    }

    [Fact]
    public void Provider_InvalidFileValue_FallsBackToDefault_AndLogsOnce()
    {
        File.WriteAllText(SettingsPath, """
            { "version": 1, "connections": [], "models": [], "theme": "system", "notes": { "folder": "relative/path" } }
            """);
        var logger = new CapturingLogger<SettingsNoteFolderProvider>();
        var provider = new SettingsNoteFolderProvider(NewCatalog(), logger);

        Assert.Equal(NoteFolderRules.DefaultFolder, provider.Folder);
        Assert.Equal(NoteFolderRules.DefaultFolder, provider.Folder);

        var warning = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("改用預設資料夾", warning.Message);
    }

    [Fact]
    public void Infrastructure_ReplacesTheCoreDefaultFolderProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INoteFolderProvider>(new DefaultNoteFolderProvider());

        services.AddLumiInfrastructure();

        var registration = Assert.Single(services, d => d.ServiceType == typeof(INoteFolderProvider));
        Assert.Equal(typeof(SettingsNoteFolderProvider), registration.ImplementationType);
    }

    /// <summary>記下所有的記錄，測試用。</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));
    }
}
