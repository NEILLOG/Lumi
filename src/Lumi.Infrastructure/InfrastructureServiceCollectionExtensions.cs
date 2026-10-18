using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Infrastructure.Persistence;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddLumiInfrastructure(
        this IServiceCollection services, Action<PersistenceOptions>? configure = null)
    {
        var options = services.AddOptions<PersistenceOptions>();
        if (configure is not null)
            options.Configure(configure);

        services.AddSingleton<IConversationStore, SqliteConversationStore>();

        // 需要另外註冊 ISecretStore（Lumi.Platform）
        services.AddOptions<ModelCatalogOptions>();
        services.AddSingleton<IModelCatalog, JsonModelCatalog>();
        return services;
    }
}
