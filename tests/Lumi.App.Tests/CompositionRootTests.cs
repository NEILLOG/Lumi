using Lumi.App.ViewModels;
using Lumi.Core;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Platform;
using Lumi.Providers;
using Lumi.Speech;
using Lumi.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        builder.Services.AddLumiSpeech();
        builder.Services.AddLumiInfrastructure(o => o.DatabasePath = Path.Combine(_directory, "lumi.db"));
        builder.Services.Configure<Lumi.Infrastructure.Attachments.AttachmentOptions>(o => o.Directory = Path.Combine(_directory, "attachments"));
        builder.Services.Configure<ModelCatalogOptions>(o => o.SettingsPath = Path.Combine(_directory, "settings.json"));
        builder.Services.AddLumiTools(o => o.NotesDirectory = Path.Combine(_directory, "notes"));
        builder.Services.AddLumiApp();
        // 角色資料夾：測試用暫存資料夾，不要碰到使用者真正的 sprites/
        builder.Services.Replace(ServiceDescriptor.Singleton(new Lumi.Sprites.Assets.AssetSpriteOptions { Directory = Path.Combine(_directory, "sprites") }));
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
        // 示範用的筆記工具預設關；檔案工具（需要使用者加入範圍）預設開
        Assert.Equal(["calculate", "get_current_time", "list_dir", "read_file", "search", "show_details"],
            services.GetRequiredService<IToolRegistry>().GetAll().Select(t => t.Descriptor.Name));
        // M5：四個 prompt 層都組進去了
        Assert.Equal([1, 2, 3, 4], services.GetServices<Lumi.Core.Prompts.IPromptLayer>().Select(l => l.Order).Order());
        Assert.IsType<Lumi.Core.Prompts.PromptComposer>(services.GetRequiredService<Lumi.Core.Prompts.IPromptComposer>());
        Assert.IsType<FileScopeFactory>(services.GetRequiredService<IFileScopeFactory>());
        Assert.Same(services.GetRequiredService<IConversationStore>(), services.GetRequiredService<IConversationContextStore>());
        // 4B：送出時的快照建構器、圖片附件存放與孤兒清理
        Assert.IsType<ContextAttachmentBuilder>(services.GetRequiredService<IContextAttachmentBuilder>());
        Assert.IsType<Lumi.Infrastructure.Attachments.FileAttachmentStore>(services.GetRequiredService<IAttachmentStore>());
        Assert.Same(services.GetRequiredService<IConversationStore>(), services.GetRequiredService<IAttachmentReferences>());
        Assert.Contains(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), h => h is Lumi.App.Services.AttachmentCleanupService);
        // M7 7A：目前的對話與回應流程是單例，工作台（以及之後的小精靈）共用同一個
        var session = services.GetRequiredService<IChatSession>();
        Assert.Same(session, services.GetRequiredService<IChatSession>());
        // M7 7D：兩種模式互斥的規則集中在協調者，工作台與協調者共用同一個；角色語氣讀目前角色
        var modes = services.GetRequiredService<Lumi.App.Services.IAppModeCoordinator>();
        Assert.Same(modes, services.GetRequiredService<Lumi.App.Services.IAppModeCoordinator>());
        Assert.IsType<Lumi.App.Views.WindowHost>(services.GetRequiredService<Lumi.App.Services.IWindowHost>());
        // UD-1：設定視窗的「回到對話」要用協調者開工作台；設定視窗服務與設定 ViewModel 能解析（沒有循環相依）
        Assert.IsType<Lumi.App.Views.SettingsWindowService>(services.GetRequiredService<Lumi.App.ViewModels.ISettingsWindowService>());
        Assert.NotNull(services.GetRequiredService<Lumi.App.ViewModels.SettingsViewModel>());
        Assert.IsType<Lumi.App.Sprites.SpriteVoiceProvider>(services.GetRequiredService<Lumi.Core.Prompts.ISpriteVoiceProvider>());
        Assert.Equal([Lumi.Sprites.Orb.OrbSpriteFactory.SpriteId], services.GetRequiredService<Lumi.Sprites.Abstractions.ISpriteCatalog>().All.Select(f => f.Manifest.Id));
        // M8：角色目錄可以重新載入（素材型角色），而且和殼、設定頁共用同一份
        var library = services.GetRequiredService<Lumi.Sprites.Assets.IReloadableSpriteCatalog>();
        Assert.Same(library, services.GetRequiredService<Lumi.Sprites.Abstractions.ISpriteCatalog>());
        Assert.Equal(Path.Combine(_directory, "sprites"), library.Directory);
        await library.ReloadAsync();   // 資料夾還不存在：視為空，不是錯誤
        Assert.Equal([Lumi.Sprites.Orb.OrbSpriteFactory.SpriteId], library.All.Select(f => f.Manifest.Id));
        Assert.Empty(library.Issues);
        Assert.IsType<Lumi.App.Services.ProcessFolderOpener>(services.GetRequiredService<IFolderOpener>());
        var vm = services.GetRequiredService<WorkbenchViewModel>();
        Assert.True(vm.HasModeCoordinator);

        await vm.InitializeAsync();

        Assert.NotNull(session.ConversationId);   // 工作台初始化走的是共用的會話，不是自己建一個

        Assert.True(File.Exists(Path.Combine(_directory, "settings.json")));
        Assert.Equal(JsonModelCatalog.MigratedConnectionId, catalog.Resolve(catalog.Current.WorkbenchModelId)!.Connection.Id);
        Assert.Equal("Claude Opus 5.5", vm.ModelName);
        Assert.NotNull(await services.GetRequiredService<IConversationStore>().GetLatestAsync());
    }
}
