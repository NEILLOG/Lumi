namespace Lumi.Core.Mcp;

/// <summary>伺服器最近一次列出的工具（M16 D12）：伺服器沒連上時，工具清單頁照樣顯示。</summary>
public sealed record McpCachedTool(string Name, string? Title, string Description, string InputSchemaJson, McpToolHints Hints);

public interface IMcpToolCache
{
    Task<IReadOnlyList<McpCachedTool>> GetAsync(string serverId, CancellationToken ct);

    Task SaveAsync(string serverId, IReadOnlyList<McpCachedTool> tools, CancellationToken ct);

    Task DeleteAsync(string serverId, CancellationToken ct);
}

/// <summary>沒有真正的儲存時的預設：什麼都不記。</summary>
public sealed class NullMcpToolCache : IMcpToolCache
{
    public Task<IReadOnlyList<McpCachedTool>> GetAsync(string serverId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<McpCachedTool>>([]);

    public Task SaveAsync(string serverId, IReadOnlyList<McpCachedTool> tools, CancellationToken ct) => Task.CompletedTask;

    public Task DeleteAsync(string serverId, CancellationToken ct) => Task.CompletedTask;
}
