using Lumi.Core.Context;
using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Tools;

public static class ToolsServiceCollectionExtensions
{
    public static IServiceCollection AddLumiTools(this IServiceCollection services, Action<ToolsOptions>? configure = null)
    {
        var options = new ToolsOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(options.Files);

        // 檔案範圍：對話目前加入的檔案與資料夾（取代 Core 預設的空範圍）
        services.Replace(ServiceDescriptor.Singleton<IFileScopeFactory, FileScopeFactory>());
        // 送出時把待送出的項目拍成快照（檔案文字、資料夾檔案樹、圖片）
        services.Replace(ServiceDescriptor.Singleton<IContextAttachmentBuilder, ContextAttachmentBuilder>());

        services.AddLumiTool<GetCurrentTimeTool>();
        services.AddLumiTool<CalculateTool>();
        services.AddLumiTool<ListDirTool>();
        services.AddLumiTool<ReadFileTool>();
        services.AddLumiTool<SearchTool>();
        if (options.EnableDemoTools)
        {
            services.AddLumiTool<SaveNoteTool>();
            services.AddLumiTool<DeleteNoteTool>();
        }
        return services;
    }
}
