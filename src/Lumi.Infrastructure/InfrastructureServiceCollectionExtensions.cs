using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
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

        // 需要另外註冊 ISecretStore（Lumi.Platform）
        services.AddOptions<ModelCatalogOptions>();
        services.AddSingleton<IModelCatalog, JsonModelCatalog>();
        return services;
    }
}
