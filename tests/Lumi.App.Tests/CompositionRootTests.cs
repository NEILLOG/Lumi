using Lumi.App.ViewModels;
using Lumi.Core;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Platform;
using Lumi.Providers;
using Lumi.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lumi.App.Tests;

/// <summary>
/// 用和 Program 相同的方式組 DI（不開視窗），確認每個服務都解析得到，
/// 而且第一次啟動會建立設定檔、搬家出 M1 的 Anthropic 連線。資料都放暫存資料夾。
/// </summary>
public sealed class CompositionRootTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-composition-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLumiCore();
        builder.Services.AddLumiPlatform(o =>
        {
            o.KeychainService = "io.github.neillog.lumi.tests";
            o.DpapiFilePath = Path.Combine(_directory, "secrets.json");
        });
        builder.Services.AddLumiProviders();
        builder.Services.AddLumiInfrastructure(o => o.DatabasePath = Path.Combine(_directory, "lumi.db"));
        builder.Services.Configure<ModelCatalogOptions>(o => o.SettingsPath = Path.Combine(_directory, "settings.json"));
        builder.Services.AddLumiTools(o => o.NotesDirectory = Path.Combine(_directory, "notes"));
        builder.Services.AddLumiApp();
        return builder.Build();
    }

    [SkippableFact]
    public async Task AllServicesResolve_AndFirstRunMigratesM1Connection()
    {
        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Lumi.Platform 只支援 Windows 與 macOS");
        using var host = BuildHost();
        var services = host.Services;

        var catalog = services.GetRequiredService<IModelCatalog>();
        Assert.IsType<RoutingChatProvider>(services.GetRequiredService<IChatProvider>());
        Assert.IsType<RoutingConnectionTester>(services.GetRequiredService<IConnectionTester>());
        Assert.NotNull(services.GetRequiredService<ConversationService>());
        // 權限確認：App 的實作取代 Core 預設的「一律拒絕」，而且按鈕與對話迴圈用的是同一個執行個體
        Assert.IsType<Lumi.App.Services.ToolApprovalService>(services.GetRequiredService<IToolApprovalService>());
        Assert.Same(services.GetRequiredService<IToolApprovalService>(), services.GetRequiredService<IToolApprovalResponder>());
        Assert.Equal(["calculate", "delete_note", "get_current_time", "save_note"],
            services.GetRequiredService<IToolRegistry>().GetTools(ChatMode.Workbench).Select(t => t.Descriptor.Name));
        var vm = services.GetRequiredService<WorkbenchViewModel>();

        await vm.InitializeAsync();

        Assert.True(File.Exists(Path.Combine(_directory, "settings.json")));
        Assert.Equal(JsonModelCatalog.MigratedConnectionId, catalog.Resolve(catalog.Current.WorkbenchModelId)!.Connection.Id);
        Assert.Equal("Claude Opus 5.5", vm.ModelName);
        Assert.NotNull(await services.GetRequiredService<IConversationStore>().GetLatestAsync());
    }
}
