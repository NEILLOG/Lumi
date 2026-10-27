using Lumi.Browser;
using Lumi.Mcp;
using Lumi.App.ViewModels;
using Lumi.Core;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Documents;
using Lumi.Infrastructure;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Platform;
using Lumi.Providers;
using Lumi.Speech;
using Lumi.Tools;
using Lumi.Web;
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
        builder.Services.AddLumiWeb();
        builder.Services.AddLumiProviders();
        builder.Services.AddLumiSpeech();
        builder.Services.AddLumiInfrastructure(o => o.DatabasePath = Path.Combine(_directory, "lumi.db"));
        builder.Services.Configure<Lumi.Infrastructure.Attachments.AttachmentOptions>(o => o.Directory = Path.Combine(_directory, "attachments"));
        builder.Services.Configure<ModelCatalogOptions>(o => o.SettingsPath = Path.Combine(_directory, "settings.json"));
        builder.Services.AddLumiTools();
        builder.Services.AddLumiDocuments();
        builder.Services.AddLumiMcp();
        builder.Services.AddLumiBrowser(o => o.ProfileRoot = Path.Combine(_directory, "browser"));
        builder.Services.AddLumiApp();
        // 角色資料夾：測試用暫存資料夾，不要碰到使用者真正的 sprites/
        builder.Services.Replace(ServiceDescriptor.Singleton(new Lumi.Sprites.Assets.AssetSpriteOptions { Directory = Path.Combine(_directory, "sprites") }));
        return builder.Build();
    }

    [SkippableFact]
    public async Task AllServicesResolve_AndFirstRunStartsEmpty()
    {
        Skip.IfNot(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), "Lumi.Platform 只支援 Windows 與 macOS");
        using var host = BuildHost();
        var services = host.Services;

        var catalog = services.GetRequiredService<IModelCatalog>();
        Assert.IsType<RoutingChatProvider>(services.GetRequiredService<IChatProvider>());
        Assert.IsType<RoutingConnectionTester>(services.GetRequiredService<IConnectionTester>());
        Assert.NotNull(services.GetRequiredService<ConversationService>());
        // 9A：刪除對話的入口（資料庫、權限記憶、附件清理都要能解析）
        Assert.NotNull(services.GetRequiredService<ConversationDeletionService>());
        // 權限確認：App 的實作取代 Core 預設的「一律拒絕」，而且按鈕與對話迴圈用的是同一個執行個體
        Assert.IsType<Lumi.App.Services.ToolApprovalService>(services.GetRequiredService<IToolApprovalService>());
        Assert.Same(services.GetRequiredService<IToolApprovalService>(), services.GetRequiredService<IToolApprovalResponder>());
        // 檔案工具（需要使用者加入範圍）與寫入類檔案工具預設開（M10 移除示範工具後，只剩正式工具）
        Assert.Equal(["add_note", "browser_back", "browser_click", "browser_close", "browser_download", "browser_extract", "browser_open", "browser_press", "browser_screenshot", "browser_scroll", "browser_select", "browser_snapshot", "browser_type", "browser_upload", "browser_wait_for_user", "calculate", "cancel_reminder", "conversation_search", "copy_file", "create_folder", "create_reminder", "download_file", "edit_image", "generate_image", "get_current_time", "http_request", "http_send", "list_dir", "list_reminders", "memory_delete", "memory_save", "memory_update", "move_file", "net_check", "overwrite_file", "read_document", "read_file", "read_notes", "rss_read", "search", "set_reminder_enabled", "show_details", "start_timer", "web_crawl", "web_fetch", "web_links", "write_file"],
            services.GetRequiredService<IToolRegistry>().GetAll().Select(t => t.Descriptor.Name));
        // 長期記憶（M14）：SQLite 版取代 Core 預設的記憶體版；第 5 層、清理服務都要能解析
        Assert.IsType<Lumi.Infrastructure.Persistence.SqliteMemoryStore>(services.GetRequiredService<Lumi.Core.Memory.IMemoryStore>());
        Assert.Contains(services.GetServices<Lumi.Core.Prompts.IPromptLayer>(), l => l is Lumi.Core.Prompts.MemoryPromptLayer);
        // ConversationService 的 memoryStore 是可選參數：沒被注入就會靜默失效（第 5 層永遠沒有記憶），所以直接檢查欄位
        var injected = typeof(ConversationService).GetField("_memoryStore", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(services.GetRequiredService<ConversationService>());
        Assert.Same(services.GetRequiredService<Lumi.Core.Memory.IMemoryStore>(), injected);
        Assert.Contains(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is Lumi.App.Services.MemoryCleanupService);
        // M15／M16：工具目錄、MCP 管理員、背景啟動。ConversationService 的 toolCatalog 是可選參數，沒被注入會退回自己建的一份，所以直接檢查欄位
        var toolCatalog = services.GetRequiredService<Lumi.Core.Tools.IToolCatalog>();
        Assert.IsType<Lumi.Core.Tools.ToolCatalog>(toolCatalog);
        Assert.Same(toolCatalog, typeof(ConversationService).GetField("_toolCatalog", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(services.GetRequiredService<ConversationService>()));
        Assert.IsType<Lumi.Mcp.McpServerManager>(services.GetRequiredService<Lumi.Core.Mcp.IMcpToolSource>());
        Assert.IsType<Lumi.Infrastructure.Persistence.SqliteMcpToolCache>(services.GetRequiredService<Lumi.Core.Mcp.IMcpToolCache>());
        Assert.NotNull(services.GetRequiredService<Lumi.Core.Platform.IShellEnvironment>());
        Assert.Contains(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is Lumi.App.Services.McpHostedService);
        // M17：網路基礎。IWebProxy 是 LumiWebProxy（Proxy 資訊與 HttpClient 共用同一個）、網路工具用的服務都能解析
        Assert.IsType<Lumi.Web.Proxy.LumiWebProxy>(services.GetRequiredService<System.Net.IWebProxy>());
        Assert.Same(services.GetRequiredService<System.Net.IWebProxy>(), services.GetRequiredService<Lumi.Core.Web.INetworkProxyInfo>());
        Assert.NotNull(services.GetRequiredService<Lumi.Core.Web.IWebClient>());
        Assert.NotNull(services.GetRequiredService<Lumi.Core.Web.IWebContentExtractor>());
        Assert.NotNull(services.GetRequiredService<Lumi.Core.Web.IFeedReader>());
        Assert.NotNull(services.GetRequiredService<Lumi.Core.Web.INetworkDiagnostics>());
        Assert.NotNull(services.GetRequiredService<Lumi.Core.Web.INetworkTester>());
        Assert.NotNull(services.GetRequiredService<NetworkViewModel>());
        Assert.Contains(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is Lumi.Web.Proxy.ProxyHostedService);
        // M18：瀏覽器。Core 的 NullBrowserSessions 被取代；Playwright 的工作階段是單例，刪除對話與對話迴圈用的是同一個。
        // 這兩個可選參數沒被注入會靜默失效（圖片被丟掉、刪除對話時不關瀏覽器），所以直接檢查欄位（M14 的教訓）
        Assert.IsType<PlaywrightBrowserSessions>(services.GetRequiredService<Lumi.Core.Browser.IBrowserSessions>());
        Assert.IsType<BrowserLocator>(services.GetRequiredService<Lumi.Core.Browser.IBrowserLocator>());
        Assert.Same(services.GetRequiredService<Lumi.Core.Browser.IBrowserSessions>(), typeof(ConversationDeletionService)
            .GetField("_browser", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(services.GetRequiredService<ConversationDeletionService>()));
        Assert.NotNull(typeof(ConversationService).GetField("_attachments", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(services.GetRequiredService<ConversationService>()));
        Assert.Contains(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is BrowserHostedService);
        // 筆記資料夾：設定實作取代 Core 的預設；INoteStore 由 Lumi.Tools 提供
        Assert.IsType<Lumi.Infrastructure.Settings.SettingsNoteFolderProvider>(services.GetRequiredService<Lumi.Core.Notes.INoteFolderProvider>());
        Assert.Equal("NoteStore", services.GetRequiredService<Lumi.Core.Notes.INoteStore>().GetType().Name);   // 內部類別，只比對名稱
        // M5：四個 prompt 層、M14 的第 5 層（記憶）都組進去了
        Assert.Equal([1, 2, 3, 4, 5], services.GetServices<Lumi.Core.Prompts.IPromptLayer>().Select(l => l.Order).Order());
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
        // 22B：第一次啟動沒有 Claude 預設，工作台顯示「沒有模型」，首頁會引導去設定
        Assert.Empty(catalog.Current.Connections);
        Assert.Null(catalog.Resolve(catalog.Current.WorkbenchModelId));
        Assert.Equal(Lumi.App.Resources.Strings.Workbench_NoModel, vm.ModelName);
        Assert.NotNull(await services.GetRequiredService<IConversationStore>().GetLatestAsync());
    }
}
