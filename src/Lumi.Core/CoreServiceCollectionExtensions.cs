using Lumi.Core.Chat;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>需要另外註冊 <see cref="IChatProvider"/>、<see cref="IConversationStore"/>、<see cref="ChatDefaults"/>。</summary>
    public static IServiceCollection AddLumiCore(this IServiceCollection services)
    {
        services.AddSingleton<ConversationService>();
        return services;
    }
}
