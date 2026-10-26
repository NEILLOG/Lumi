using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Notes;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Platform;
using Microsoft.Extensions.Options;

namespace Lumi.App.Tests;

/// <summary>M20 20C：「資料與隱私」的位置清單——九筆、順序、路徑來自實際的 Options，檔案與資料夾的區分，鑰匙圈的特例。</summary>
public sealed class DataLocationProviderTests : IDisposable
{
    private static readonly string[] ExpectedIds =
        ["data", "database", "attachments", "settings", "secrets", "logs", "browser", "sprites", "notes"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-locations-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Sub(string name) => Path.Combine(_root, name);

    private DataLocationProvider NewProvider(out PersistenceOptions persistence, out AttachmentOptions attachments,
        out ModelCatalogOptions models, out SecretStoreOptions secrets, string notesFolder)
    {
        persistence = new PersistenceOptions { DatabasePath = Sub("lumi.db") };
        attachments = new AttachmentOptions { Directory = Sub("attachments") };
        models = new ModelCatalogOptions { SettingsPath = Sub("settings.json") };
        secrets = new SecretStoreOptions { DpapiFilePath = Sub("secrets.json") };
        return new DataLocationProvider(
            Options.Create(persistence),
            Options.Create(attachments),
            Options.Create(models),
            Options.Create(secrets),
            new FixedNoteFolder(notesFolder));
    }

    [Fact]
    public void GetLocations_ReturnsNineEntries_InTheDisplayOrder()
    {
        var provider = NewProvider(out _, out _, out _, out _, Sub("notes"));

        var locations = provider.GetLocations();

        Assert.Equal(ExpectedIds, locations.Select(l => l.Id));
    }

    [Fact]
    public void GetLocations_PathsComeFromTheResolvedOptions()
    {
        var provider = NewProvider(out var persistence, out var attachments, out var models, out _, Sub("notes"));

        var byId = provider.GetLocations().ToDictionary(l => l.Id);

        Assert.Equal(LumiPaths.DataDirectory, byId["data"].Path);
        Assert.Equal(persistence.ResolveDatabasePath(), byId["database"].Path);
        Assert.Equal(attachments.ResolveDirectory(), byId["attachments"].Path);
        Assert.Equal(models.ResolveSettingsPath(), byId["settings"].Path);
        Assert.Equal(LumiPaths.LogDirectory, byId["logs"].Path);
        Assert.Equal(Path.Combine(LumiPaths.DataDirectory, "browser"), byId["browser"].Path);
        Assert.Equal(Path.Combine(LumiPaths.DataDirectory, "sprites"), byId["sprites"].Path);
        Assert.Equal(Sub("notes"), byId["notes"].Path);
    }

    [Fact]
    public void GetLocations_DatabaseAndSettingsAreFiles_FoldersAreNot()
    {
        var provider = NewProvider(out _, out _, out _, out _, Sub("notes"));

        var byId = provider.GetLocations().ToDictionary(l => l.Id);

        Assert.True(byId["database"].IsFile);
        Assert.True(byId["settings"].IsFile);
        foreach (var id in new[] { "data", "attachments", "logs", "browser", "sprites", "notes" })
            Assert.False(byId[id].IsFile, id);
    }

    [Fact]
    public void GetLocations_NotesComesFromTheNoteFolderProvider()
    {
        var provider = NewProvider(out _, out _, out _, out _, Sub("my-notes"));

        var notes = provider.GetLocations().Single(l => l.Id == "notes");

        Assert.Equal(Sub("my-notes"), notes.Path);
        Assert.Equal(Strings.Privacy_LocNotes, notes.Name);
    }

    [Fact]
    public void GetLocations_Secrets_FollowThePlatform()
    {
        var provider = NewProvider(out _, out _, out _, out var secrets, Sub("notes"));

        var secretsLocation = provider.GetLocations().Single(l => l.Id == "secrets");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Strings.Privacy_LocSecretsWindows, secretsLocation.Name);
            Assert.Equal(Strings.Privacy_LocSecretsWindowsDesc, secretsLocation.Description);
            Assert.Equal(secrets.ResolveDpapiFilePath(), secretsLocation.Path);
            Assert.True(secretsLocation.IsFile);
        }
        else
        {
            // macOS：金鑰在鑰匙圈，沒有路徑可以開啟，說明要寫出服務名稱
            Assert.Equal(Strings.Privacy_LocSecretsMac, secretsLocation.Name);
            Assert.Null(secretsLocation.Path);
            Assert.False(secretsLocation.IsFile);
            Assert.Contains("io.github.neillog.lumi", secretsLocation.Description);
        }
    }

    private sealed class FixedNoteFolder(string folder) : INoteFolderProvider
    {
        public string Folder { get; } = folder;
    }
}
