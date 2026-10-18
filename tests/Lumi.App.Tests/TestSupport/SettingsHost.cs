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

    public SettingsHost(string? settingsFileContent = null)
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
        General = new GeneralViewModel(Catalog, dispatcher);
        Settings = new SettingsViewModel(Connections, Models, General, Catalog, Confirm);
    }

    public string SettingsPath { get; }
    public InMemorySecretStore Secrets { get; } = new();
    public FakeConnectionTester Tester { get; } = new();
    public FakeConfirmService Confirm { get; } = new();
    public ConnectionStatusCache Status { get; } = new();
    public JsonModelCatalog Catalog { get; }
    public ConnectionsViewModel Connections { get; }
    public ModelsViewModel Models { get; }
    public GeneralViewModel General { get; }
    public SettingsViewModel Settings { get; }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    public static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
