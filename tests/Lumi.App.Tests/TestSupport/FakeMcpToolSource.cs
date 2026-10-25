using Lumi.Core.Mcp;

namespace Lumi.App.Tests.TestSupport;

/// <summary>可以設定狀態、觸發 <see cref="Changed"/>、控制 <see cref="TestAsync"/> 結果的假 MCP 來源。</summary>
internal sealed class FakeMcpToolSource : IMcpToolSource
{
    public List<McpServerStatus> Statuses { get; } = [];

    /// <summary>下一次 <see cref="TestAsync"/> 回傳的結果。</summary>
    public McpTestResult NextTestResult { get; set; } = new(true, null, []);

    /// <summary>每次 <see cref="TestAsync"/> 收到的設定與祕密（測試用，不寫 log）。</summary>
    public List<(McpServerSettings Server, McpServerSecrets Secrets)> TestCalls { get; } = [];

    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<IMcpTool> GetTools() => [];

    public IReadOnlyList<McpServerStatus> GetStatuses() => Statuses.ToList();

    public Task WaitForStartupAsync(TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;

    public Task<McpTestResult> TestAsync(McpServerSettings server, McpServerSecrets secrets, CancellationToken ct)
    {
        TestCalls.Add((server, secrets));
        return Task.FromResult(NextTestResult);
    }

    public Task RestartAsync(string serverId, CancellationToken ct) => Task.CompletedTask;
}
