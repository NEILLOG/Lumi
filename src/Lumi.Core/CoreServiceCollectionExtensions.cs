using Lumi.Core.Chat;
using Lumi.Core.Context;
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

        // 檔案範圍：Lumi.Tools 與 Lumi.Infrastructure 註冊真正的實作；沒有的時候是空的（沒有範圍就沒有檔案工具）
        services.TryAddSingleton<IFileScopeFactory, EmptyFileScopeFactory>();
        services.TryAddSingleton<IConversationContextStore, NullConversationContextStore>();
        services.TryAddSingleton<IContextAttachmentBuilder, NullContextAttachmentBuilder>();
        services.TryAddSingleton<IAttachmentStore, NullAttachmentStore>();
        services.TryAddSingleton<IContextInspector, NullContextInspector>();
        services.TryAddSingleton<ITokenEstimator, HeuristicTokenEstimator>();
        return services;
    }
}
