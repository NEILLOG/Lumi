using System.Globalization;
using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Notes;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Platform;
using Microsoft.Extensions.Options;

namespace Lumi.App.Services;

/// <summary>
/// 「資料與隱私」的位置清單（M20 20C）：每次呼叫都用目前的 Options 解析實際路徑，順序就是設定頁的顯示順序。
/// 瀏覽器與角色素材的資料夾沒有註冊成 <see cref="IOptions{TOptions}"/>，這裡直接用 App 組裝時設定的同一個路徑
/// （<c>Program.cs</c> 與 <c>AppServiceCollectionExtensions.cs</c>）。
/// </summary>
public sealed class DataLocationProvider(
    IOptions<PersistenceOptions> persistence,
    IOptions<AttachmentOptions> attachments,
    IOptions<ModelCatalogOptions> models,
    IOptions<SecretStoreOptions> secrets,
    INoteFolderProvider notes) : IDataLocationProvider
{
    public IReadOnlyList<DataLocation> GetLocations() =>
    [
        new("data", Strings.Privacy_LocData, Strings.Privacy_LocDataDesc, LumiPaths.DataDirectory, IsFile: false),
        new("database", Strings.Privacy_LocDatabase, Strings.Privacy_LocDatabaseDesc, persistence.Value.ResolveDatabasePath(), IsFile: true),
        new("attachments", Strings.Privacy_LocAttachments, Strings.Privacy_LocAttachmentsDesc, attachments.Value.ResolveDirectory(), IsFile: false),
        new("settings", Strings.Privacy_LocSettings, Strings.Privacy_LocSettingsDesc, models.Value.ResolveSettingsPath(), IsFile: true),
        SecretsLocation(secrets.Value),
        new("logs", Strings.Privacy_LocLogs, Strings.Privacy_LocLogsDesc, LumiPaths.LogDirectory, IsFile: false),
        new("browser", Strings.Privacy_LocBrowser, Strings.Privacy_LocBrowserDesc, Path.Combine(LumiPaths.DataDirectory, "browser"), IsFile: false),
        new("sprites", Strings.Privacy_LocSprites, Strings.Privacy_LocSpritesDesc, Path.Combine(LumiPaths.DataDirectory, "sprites"), IsFile: false),
        new("notes", Strings.Privacy_LocNotes, Strings.Privacy_LocNotesDesc, notes.Folder, IsFile: false),
    ];

    private static DataLocation SecretsLocation(SecretStoreOptions options)
    {
        if (OperatingSystem.IsWindows())
            return new DataLocation("secrets", Strings.Privacy_LocSecretsWindows, Strings.Privacy_LocSecretsWindowsDesc,
                options.ResolveDpapiFilePath(), IsFile: true);

        // macOS：金鑰在鑰匙圈裡，沒有可以開啟的檔案
        return new DataLocation("secrets", Strings.Privacy_LocSecretsMac,
            string.Format(CultureInfo.CurrentCulture, Strings.Privacy_LocSecretsMacDesc, options.KeychainService),
            Path: null, IsFile: false);
    }
}
