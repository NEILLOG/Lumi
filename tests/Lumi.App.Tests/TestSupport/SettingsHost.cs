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

    private SettingsHost(string? settingsFileContent, Lumi.Sprites.Abstractions.ISpriteCatalog spriteCatalog, IFolderOpener? folders)
    {
        SettingsPath = Path.Combine(_directory, "settings.json");
        if (settingsFileContent is not null)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(SettingsPath, settingsFileContent);
        }

        Catalog = new JsonModelCatalog(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }),
            Secrets, NullLogger<JsonModelCatalog>.Instance);
        var dispatcher = new SyncUiDispatcher();
        Connections = new ConnectionsViewModel(Catalog, Tester, Status, Confirm, dispatcher);
        Models = new ModelsViewModel(Catalog, Tester, Status, Confirm, dispatcher);
        Persona = new PersonaViewModel(Catalog, dispatcher);
        General = new GeneralViewModel(Catalog, dispatcher);
        SpriteCatalog = spriteCatalog;
        Sprite = new SpriteSettingsViewModel(Catalog, SpriteCatalog, dispatcher, folders);
        Settings = new SettingsViewModel(Connections, Models, Persona, General, Sprite, Catalog, Confirm, WindowService);
    }

    public string SettingsPath { get; }
    public FakeSettingsWindowService WindowService { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public FakeConnectionTester Tester { get; } = new();
    public FakeConfirmService Confirm { get; } = new();
    public ConnectionStatusCache Status { get; } = new();
    public JsonModelCatalog Catalog { get; }
    public ConnectionsViewModel Connections { get; }
    public ModelsViewModel Models { get; }
    public PersonaViewModel Persona { get; }
    public GeneralViewModel General { get; }
    public SpriteSettingsViewModel Sprite { get; }
    public Lumi.Sprites.Abstractions.ISpriteCatalog SpriteCatalog { get; }
    public SettingsViewModel Settings { get; }

    public void Dispose()
    {
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
