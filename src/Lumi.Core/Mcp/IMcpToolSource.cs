using Lumi.Core.Tools;

namespace Lumi.Core.Mcp;

public enum McpServerState { Disabled, Connecting, Ready, Failed }

public sealed record McpServerStatus(string ServerId, McpServerState State, string? Error, int ToolCount);

/// <summary>伺服器對工具的標示（annotations）。只是建議，不可信（M16 D3）。</summary>
public sealed record McpToolHints(bool? ReadOnly, bool? Destructive);

public sealed record McpTestResult(bool Success, string? Error, IReadOnlyList<string> ToolNames);

/// <summary>MCP 工具的來源（實作在 Lumi.Mcp 的 <c>McpServerManager</c>）。</summary>
public interface IMcpToolSource
{
    /// <summary>已啟用伺服器的全部 MCP 工具（連線中的用即時清單，其他用快取）。</summary>
    IReadOnlyList<IMcpTool> GetTools();

    IReadOnlyList<McpServerStatus> GetStatuses();

    /// <summary>等到沒有伺服器處於 Connecting，或逾時。</summary>
    Task WaitForStartupAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>用尚未儲存的設定測試：連線、列出工具、斷線。不影響目前的連線。</summary>
    Task<McpTestResult> TestAsync(McpServerSettings server, McpServerSecrets secrets, CancellationToken ct);

    Task RestartAsync(string serverId, CancellationToken ct);

    /// <summary>狀態或工具清單改變時觸發（可能在背景執行緒）。</summary>
    event EventHandler? Changed;
}

/// <summary>MCP 工具（<c>Lumi.Mcp</c> 的 <c>McpTool</c> 實作）。</summary>
public interface IMcpTool : ITool
{
    string ServerId { get; }

    /// <summary>true＝伺服器目前連線中、清單是即時的；false＝來自快取。</summary>
    bool IsLive { get; }

    McpToolHints Hints { get; }
}

/// <summary>沒有 MCP 時的預設（測試與尚未組裝時）。</summary>
public sealed class EmptyMcpToolSource : IMcpToolSource
{
    public IReadOnlyList<IMcpTool> GetTools() => [];

    public IReadOnlyList<McpServerStatus> GetStatuses() => [];

    public Task WaitForStartupAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;

    public Task<McpTestResult> TestAsync(McpServerSettings server, McpServerSecrets secrets, CancellationToken ct) =>
        Task.FromResult(new McpTestResult(false, "沒有 MCP 支援", []));

    public Task RestartAsync(string serverId, CancellationToken ct) => Task.CompletedTask;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
