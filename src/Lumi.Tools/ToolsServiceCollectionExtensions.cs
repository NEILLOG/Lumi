using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tools;

public static class ToolsServiceCollectionExtensions
{
    public static IServiceCollection AddLumiTools(this IServiceCollection services, Action<ToolsOptions>? configure = null)
    {
        var options = new ToolsOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        services.AddLumiTool<GetCurrentTimeTool>();
        services.AddLumiTool<CalculateTool>();
        if (options.EnableDemoTools)
        {
            services.AddLumiTool<SaveNoteTool>();
            services.AddLumiTool<DeleteNoteTool>();
        }
        return services;
    }
}
