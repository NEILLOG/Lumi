using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Notes;
using Lumi.Core.Reminders;
using Lumi.Infrastructure.Attachments;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
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
