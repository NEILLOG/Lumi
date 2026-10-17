using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
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
        return services;
    }
}
