using Lumi.App.Services;
using Lumi.App.ViewModels;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.App.Tests.TestSupport;

/// <summary>設定頁 ViewModel 的測試環境：真的 JsonModelCatalog（暫存資料夾）＋記憶體金鑰儲存＋假的測試連線與確認視窗。</summary>
internal sealed class SettingsHost : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-settings-vm-" + Guid.NewGuid().ToString("N"));

    public SettingsHost(string? settingsFileContent = null, params Lumi.Sprites.Abstractions.ISpriteFactory[] extraSprites)
        : this(settingsFileContent, new Lumi.Sprites.Abstractions.SpriteCatalog([new Lumi.Sprites.Orb.OrbSpriteFactory(), .. extraSprites]), null)
    {
    }

    /// <summary>用指定的角色目錄（例如真的 <c>SpriteLibrary</c>）與資料夾開啟器。</summary>
    public SettingsHost(Lumi.Sprites.Abstractions.ISpriteCatalog spriteCatalog, IFolderOpener? folders = null)
        : this(null, spriteCatalog, folders)
    {
    }

    /// <summary>用指定的 UI 派送器（例如真的 Avalonia 的），讓目錄變動的事件在 UI 執行緒上更新畫面；預設是同步執行的假派送器。</summary>
    public SettingsHost(IUiDispatcher dispatcher)
        : this(null, new Lumi.Sprites.Abstractions.SpriteCatalog([new Lumi.Sprites.Orb.OrbSpriteFactory()]), null, dispatcher)
    {
    }

    private SettingsHost(string? settingsFileContent, Lumi.Sprites.Abstractions.ISpriteCatalog spriteCatalog, IFolderOpener? folders,
        IUiDispatcher? uiDispatcher = null)
    {
        SettingsPath = Path.Combine(_directory, "settings.json");
        if (settingsFileContent is not null)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(SettingsPath, settingsFileContent);
        }

        Catalog = new JsonModelCatalog(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }),
            Secrets, NullLogger<JsonModelCatalog>.Instance);
        var dispatcher = uiDispatcher ?? new SyncUiDispatcher();
        Connections = new ConnectionsViewModel(Catalog, Tester, Status, Confirm, dispatcher);
        Models = new ModelsViewModel(Catalog, Tester, Status, Confirm, dispatcher);
        Persona = new PersonaViewModel(Catalog, dispatcher);
        Memory = new MemoryViewModel(MemoryStore, Catalog, Confirm, dispatcher, new FakeTimeProvider());
        Tools = new ToolsViewModel(new FakeToolCatalog(
            FakeCatalogTool.Make("read_file", "讀取檔案", group: Lumi.Core.Tools.ToolGroups.FilesRead),
            FakeCatalogTool.Make("write_file", "寫入檔案", Lumi.Core.Tools.ToolPermission.Write, group: Lumi.Core.Tools.ToolGroups.FilesWrite),
            FakeCatalogTool.Make("overwrite_file", "覆寫檔案", Lumi.Core.Tools.ToolPermission.Destructive, group: Lumi.Core.Tools.ToolGroups.FilesWrite)), Catalog, Confirm, McpSource, dispatcher);
        McpServers = new McpServersViewModel(Catalog, McpSource, Confirm, dispatcher);
        General = new GeneralViewModel(Catalog, dispatcher);
        SpriteCatalog = spriteCatalog;
        Sprite = new SpriteSettingsViewModel(Catalog, SpriteCatalog, dispatcher, folders);
        Speech = new SpeechViewModel(Catalog, SpeechTester, Secrets, Confirm, dispatcher);
        Network = new NetworkViewModel(Catalog, NetworkTester, dispatcher);
        Browser = new BrowserSettingsViewModel(Catalog, BrowserLocator, BrowserSessions, Confirm, dispatcher);
        ReminderService = new Lumi.Core.Reminders.ReminderService(ReminderStore, new NullNotificationSink(), TimeProvider.System,
            new Lumi.Core.Reminders.ReminderOptions(), NullLogger<Lumi.Core.Reminders.ReminderService>.Instance);
        Reminders = new RemindersViewModel(ReminderService, Confirm, dispatcher, TimeProvider.System);
        Privacy = new DataPrivacyViewModel(DataLocations, folders ?? new FakeFolderOpener(), Clipboard);
        Settings = new SettingsViewModel(Connections, Models, Persona, Memory, Tools, McpServers, General, Sprite, Speech, Network, Browser, Reminders, Privacy, Catalog, Confirm, WindowService);
    }

    public string SettingsPath { get; }
    public Lumi.Core.Reminders.InMemoryScheduledItemStore ReminderStore { get; } = new();
    public Lumi.Core.Reminders.ReminderService ReminderService { get; }
    public RemindersViewModel Reminders { get; }
    public FakeDataLocationProvider DataLocations { get; } = new();
    public FakeClipboardService Clipboard { get; } = new();
    public DataPrivacyViewModel Privacy { get; }
    public FakeSettingsWindowService WindowService { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public FakeConnectionTester Tester { get; } = new();
    public FakeConfirmService Confirm { get; } = new();
    public FakeNetworkTester NetworkTester { get; } = new();
    public NetworkViewModel Network { get; }
    public FakeBrowserLocator BrowserLocator { get; } = new();
    public Lumi.App.Tests.TestSupport.FakeBrowserSessions BrowserSessions { get; } = new();
    public BrowserSettingsViewModel Browser { get; }
    public ConnectionStatusCache Status { get; } = new();
    public JsonModelCatalog Catalog { get; }
    public ConnectionsViewModel Connections { get; }
    public ModelsViewModel Models { get; }
    public PersonaViewModel Persona { get; }
    public FakeMemoryStore MemoryStore { get; } = new();
    public MemoryViewModel Memory { get; }
    public ToolsViewModel Tools { get; }
    public McpServersViewModel McpServers { get; }
    public FakeMcpToolSource McpSource { get; } = new();
    public GeneralViewModel General { get; }
    public SpriteSettingsViewModel Sprite { get; }
    public SpeechViewModel Speech { get; }
    public FakeSpeechConnectionTester SpeechTester { get; } = new();
    public Lumi.Sprites.Abstractions.ISpriteCatalog SpriteCatalog { get; }
    public SettingsViewModel Settings { get; }

    public void Dispose()
    {
        Memory.Dispose();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    public static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !Check(condition); i++)
            await Task.Delay(10);
        Assert.True(Check(condition));
    }

    /// <summary>
    /// 回應的事件在背景執行緒上更新畫面的清單（正式程式有 UI 同步內容，測試沒有），輪詢條件時可能剛好撞上清單被修改：
    /// 那一次就當作「還沒好」，下一輪再看。
    /// </summary>
    private static bool Check(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class NullNotificationSink : Lumi.Core.Reminders.INotificationSink
{
    public Task ShowAsync(Lumi.Core.Reminders.AppNotification notification, CancellationToken ct) => Task.CompletedTask;
}

internal sealed class FakeDataLocationProvider : IDataLocationProvider
{
    public List<DataLocation> Locations { get; } = [];

    public IReadOnlyList<DataLocation> GetLocations() => Locations;
}
