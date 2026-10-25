using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Mcp;

public static class McpServiceCollectionExtensions
{
    /// <summary>註冊 MCP 的服務。組裝內容在 M16 整合時填入（Phase 0 只放入口）。</summary>
    public static IServiceCollection AddLumiMcp(this IServiceCollection services) => services;
}
