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
