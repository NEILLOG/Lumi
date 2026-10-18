using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>需要另外註冊 <see cref="IChatProvider"/>、<see cref="IConversationStore"/>、<see cref="Models.IModelCatalog"/>。</summary>
    public static IServiceCollection AddLumiCore(this IServiceCollection services)
    {
        services.AddSingleton<ConversationService>();

        // 工具：登錄、權限記憶（只在記憶體）、迴圈設定。沒有畫面可以詢問時預設一律拒絕（TryAdd：App 註冊自己的會取代它）
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<ToolPermissionMemory>();
        services.AddSingleton(new ToolLoopOptions());
        services.TryAddSingleton<IToolApprovalService, DenyAllToolApprovalService>();
        return services;
    }
}
