using Lumi.Core.Mcp;
using Lumi.Core.Memory;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Notes;
using Lumi.Core.Reminders;
using Lumi.Core.Skills;
using Lumi.Core.Usage;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Lumi.Infrastructure.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddLumiInfrastructure(
        this IServiceCollection services, Action<PersistenceOptions>? configure = null)
    {
        var options = services.AddOptions<PersistenceOptions>();
        if (configure is not null)
            options.Configure(configure);

        // 同一個類別實作對話與上下文兩個介面：共用連線字串與 schema 初始化
        services.AddSingleton<SqliteConversationStore>();
        services.AddSingleton<IConversationStore>(sp => sp.GetRequiredService<SqliteConversationStore>());
        services.Replace(ServiceDescriptor.Singleton<IConversationContextStore>(sp => sp.GetRequiredService<SqliteConversationStore>()));
        services.AddSingleton<IAttachmentReferences>(sp => sp.GetRequiredService<SqliteConversationStore>());
        services.AddSingleton<SearchTextBackfill>();   // 啟動後的背景補寫（App 的 SearchTextBackfillService 呼叫）

        // 提醒與排程（M10 10D）：同一個資料庫，共用上面的連線與 schema 初始化
        services.AddSingleton<SqliteScheduledItemStore>();
        services.AddSingleton<IScheduledItemStore>(sp => sp.GetRequiredService<SqliteScheduledItemStore>());

        // 排程跑 prompt 的執行紀錄（M24）：同一個資料庫
        services.AddSingleton<SqliteScheduledRunStore>();
        services.AddSingleton<IScheduledRunStore>(sp => sp.GetRequiredService<SqliteScheduledRunStore>());

        // 長期記憶（M14）：同一個資料庫；取代 Core 預設的記憶體版
        services.AddSingleton<SqliteMemoryStore>();
        services.Replace(ServiceDescriptor.Singleton<IMemoryStore>(sp => sp.GetRequiredService<SqliteMemoryStore>()));
        services.AddSingleton<IMcpToolCache, SqliteMcpToolCache>();   // MCP 伺服器最近一次的工具清單（M16）

        // 用量與費用（M23）：每次請求一列，同一個資料庫；紀錄器與預算監看建在上面
        services.AddSingleton<SqliteUsageStore>();
        services.AddSingleton<IUsageStore>(sp => sp.GetRequiredService<SqliteUsageStore>());
        services.AddSingleton<IUsageRecorder, UsageRecorder>();
        services.AddSingleton<BudgetMonitor>();
        services.Replace(ServiceDescriptor.Singleton<IUsageHintLog>(sp => ActivatorUtilities.CreateInstance<SqliteUsageHintLog>(sp)));   // 使用習慣提示的紀錄；取代 Core 預設的 NullUsageHintLog

        // 技能（M29）：檔案存在使用者資料夾的 skills/；取代 Core 預設的記憶體版
        services.AddOptions<SkillStoreOptions>();
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<FileSkillStore>(sp,
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SkillStoreOptions>>().Value.ResolveDirectory()));
        services.Replace(ServiceDescriptor.Singleton<ISkillStore>(sp => sp.GetRequiredService<FileSkillStore>()));
        // 執行指令的擋掉清單（M31）：要知道技能資料夾在哪裡，才能提醒「指令會動到技能資料夾」
        services.AddSingleton<Lumi.Core.Commands.ICommandGuard>(sp => new Lumi.Core.Commands.CommandGuard(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SkillStoreOptions>>().Value.ResolveDirectory()));

        // 圖片附件：檔案存在使用者資料夾的 attachments/
        services.AddOptions<AttachmentOptions>();
        services.Replace(ServiceDescriptor.Singleton<IAttachmentStore, FileAttachmentStore>());
        services.AddSingleton<AttachmentCleaner>();

        // 需要另外註冊 ISecretStore（Lumi.Platform）
        services.AddOptions<ModelCatalogOptions>();
        services.AddSingleton<IModelCatalog, JsonModelCatalog>();

        // 筆記資料夾（M10 10B）：從設定讀，取代 Core 的預設實作（Lumi.Tools 的註冊是 TryAdd，順序不影響結果）
        services.Replace(ServiceDescriptor.Singleton<INoteFolderProvider, SettingsNoteFolderProvider>());
        return services;
    }
}
