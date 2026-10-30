using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Mcp;
using Lumi.Core.Memory;
using Lumi.Core.Network;
using Lumi.Core.Prompts;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Core.Updates;
using Lumi.Core.Usage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>需要另外註冊 <see cref="IChatProvider"/>、<see cref="IConversationStore"/>、<see cref="Models.IModelCatalog"/>。</summary>
    public static IServiceCollection AddLumiCore(this IServiceCollection services)
    {
        services.AddSingleton<ConversationService>();
        services.AddSingleton<SpriteConversationService>();   // 小精靈專屬對話的 id 與建立（M22）
        services.AddSingleton<IChatSession, ChatSession>();   // 工作台與小精靈共用的目前對話與回應流程
        services.AddSingleton<ConversationDeletionService>(); // 刪除對話：資料庫 → 權限記憶 → 附件（需要 AttachmentCleaner，由 Lumi.Infrastructure 註冊）

        // 工具：登錄、權限記憶（只在記憶體）、迴圈設定。沒有畫面可以詢問時預設一律拒絕（TryAdd：App 註冊自己的會取代它）
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IToolCatalog, ToolCatalog>();   // 工具目錄：設定頁與對話迴圈共用（M15）
        services.AddSingleton<ToolPermissionMemory>();
        services.AddSingleton(new ToolLoopOptions());
        services.TryAddSingleton<IToolApprovalService, DenyAllToolApprovalService>();

        // 檔案範圍：Lumi.Tools 與 Lumi.Infrastructure 註冊真正的實作；沒有的時候是空的（沒有範圍就沒有檔案工具）
        services.TryAddSingleton<IFileScopeFactory, EmptyFileScopeFactory>();
        services.TryAddSingleton<IConversationContextStore, NullConversationContextStore>();
        services.TryAddSingleton<IContextAttachmentBuilder, NullContextAttachmentBuilder>();
        services.TryAddSingleton<IAttachmentStore, NullAttachmentStore>();
        services.TryAddSingleton<IContextInspector, NullContextInspector>();
        services.TryAddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        services.TryAddSingleton<IUpdateChecker, NullUpdateChecker>();     // 檢查新版的來源；Lumi.Web 的 AddLumiWeb 取代成 GitHub Releases 版
        services.AddSingleton<IUpdateService, UpdateService>();            // 啟動時與設定頁「立即檢查」共用，記住略過的版本
        services.TryAddSingleton<IUsageHintLog, NullUsageHintLog>();       // 使用習慣提示的紀錄；Lumi.Infrastructure 取代成 SQLite 版（M23）
        services.TryAddSingleton<INetworkStatus, SystemNetworkStatus>();   // 失敗後補「看起來沒有網路連線」（M9 9C）；測試可換成假的

        // Prompt 分層（agent.md 第 9 節）：四個內建層；角色語氣到 M7 才有角色提供
        services.AddSingleton<IPromptComposer, PromptComposer>();
        services.AddSingleton(new PromptOptions());
        services.TryAddSingleton<ISpriteVoiceProvider, NoSpriteVoiceProvider>();
        services.AddLumiPromptLayer<BasePromptLayer>();
        services.AddLumiPromptLayer<PersonaPromptLayer>();
        services.AddLumiPromptLayer<ModeFormatPromptLayer>();
        services.AddLumiPromptLayer<SpriteVoicePromptLayer>();
        services.AddLumiPromptLayer<MemoryPromptLayer>();   // 第 5 層：長期記憶（M14）

        // 長期記憶（M14）：預設是記憶體版（測試與沒有資料庫時）；Lumi.Infrastructure 取代成 SQLite 版
        services.TryAddSingleton(new MemoryOptions());
        services.TryAddSingleton<IMemoryStore, InMemoryMemoryStore>();
        services.TryAddSingleton<Lumi.Core.Browser.IBrowserSessions, Lumi.Core.Browser.NullBrowserSessions>();   // 沒有瀏覽器時的預設；Lumi.Browser 的 AddLumiBrowser 會取代（M18）
        services.TryAddSingleton<IMcpToolSource, EmptyMcpToolSource>();   // 沒有 MCP 時的預設；Lumi.Mcp 的 AddLumiMcp 會取代（M16）

        // 提醒與排程（M10 10D）：服務本體；IScheduledItemStore 由 Lumi.Infrastructure 註冊，通知由 App 取代 NullNotificationSink
        services.TryAddSingleton(new ReminderOptions());
        services.TryAddSingleton<INotificationSink, NullNotificationSink>();
        services.AddSingleton<ReminderService>();
        services.AddSingleton<IReminderService>(sp => sp.GetRequiredService<ReminderService>());
        return services;
    }

    /// <summary>註冊一層 prompt：<c>services.AddLumiPromptLayer&lt;MyLayer&gt;()</c>。</summary>
    public static IServiceCollection AddLumiPromptLayer<TLayer>(this IServiceCollection services) where TLayer : class, IPromptLayer =>
        services.AddSingleton<IPromptLayer, TLayer>();
}
