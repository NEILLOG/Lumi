using Lumi.Core.Mcp;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.TestSupport;

/// <summary>可程式化的 MCP 工具來源：工具清單、啟動等待的行為都由測試設定。</summary>
public sealed class FakeMcpToolSource : IMcpToolSource
{
    public List<IMcpTool> Tools { get; } = [];

    /// <summary>記錄每次 <see cref="WaitForStartupAsync"/> 收到的逾時。</summary>
    public List<TimeSpan> WaitTimeouts { get; } = [];

    /// <summary>WaitForStartupAsync 實際要做的事（預設立即完成）。</summary>
    public Func<TimeSpan, CancellationToken, Task> OnWaitForStartup { get; set; } = (_, _) => Task.CompletedTask;

    public IReadOnlyList<IMcpTool> GetTools() => Tools.ToList();

    public IReadOnlyList<McpServerStatus> GetStatuses() => [];

    public Task WaitForStartupAsync(TimeSpan timeout, CancellationToken ct)
    {
        WaitTimeouts.Add(timeout);
        return OnWaitForStartup(timeout, ct);
    }

    public Task<McpTestResult> TestAsync(McpServerSettings server, McpServerSecrets secrets, CancellationToken ct) =>
        Task.FromResult(new McpTestResult(false, "測試用假來源", []));

    public Task RestartAsync(string serverId, CancellationToken ct) => Task.CompletedTask;

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>MCP 工具的假實作：群組為 <c>mcp:{serverId}</c>，權限預設 Write。</summary>
public sealed class FakeMcpTool(string name, string serverId, bool live = true, ToolPermission permission = ToolPermission.Write)
    : ToolBase<EchoArgs>, IMcpTool
{
    public override ToolDescriptor Descriptor =>
        new(name, name, "測試用 MCP 工具。", permission, ToolModes.Workbench, TimeSpan.FromSeconds(5), Group: "mcp:" + serverId);

    public string ServerId => serverId;

    public bool IsLive => live;

    public McpToolHints Hints => new(null, null);

    protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct) =>
        Task.FromResult(ToolResult.Ok(args.Text));
}
