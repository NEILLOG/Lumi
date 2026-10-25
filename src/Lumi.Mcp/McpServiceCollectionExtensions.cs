using Lumi.Core.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lumi.Mcp;

public static class McpServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 MCP：連線工廠與伺服器管理員（同時是 <see cref="IMcpToolSource"/>，取代 Core 的空實作）。
    /// 需要另外有 <c>IMcpToolCache</c>（Infrastructure）、<c>IShellEnvironment</c>（Platform）、<c>INotificationSink</c>（App）。
    /// 背景啟動由 App 的 <c>McpHostedService</c> 負責。
    /// </summary>
    public static IServiceCollection AddLumiMcp(this IServiceCollection services)
    {
        services.TryAddSingleton<IMcpConnectionFactory, SdkMcpConnectionFactory>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<McpServerManager>();
        services.Replace(ServiceDescriptor.Singleton<IMcpToolSource>(sp => sp.GetRequiredService<McpServerManager>()));
        return services;
    }
}
