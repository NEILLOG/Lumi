using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lumi.Core.Mcp;
using Lumi.Core.Platform;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Mcp.Tests.TestSupport;

/// <summary>假的連線工廠：依伺服器代號決定成功（給工具清單）或失敗（給例外）；每次嘗試都記下來。</summary>
internal sealed class FakeMcpConnectionFactory : IMcpConnectionFactory
{
    public List<McpServerSettings> Attempts { get; } = [];

    public List<FakeMcpConnection> Connections { get; } = [];

    /// <summary>依伺服器代號指定失敗的例外；沒有指定就成功。</summary>
    public Dictionary<string, Exception> FailWith { get; } = new(StringComparer.Ordinal);

    /// <summary>依伺服器代號指定成功時的工具清單。</summary>
    public Dictionary<string, IReadOnlyList<McpCachedTool>> Tools { get; } = new(StringComparer.Ordinal);

    /// <summary>設定後，連線會等到這個工作完成（模擬慢的啟動）。</summary>
    public Task? Gate { get; set; }

    public async Task<IMcpConnection> ConnectAsync(McpServerSettings server, McpServerSecrets secrets, IReadOnlyDictionary<string, string> baseEnvironment, CancellationToken ct)
    {
        Attempts.Add(server);
        if (Gate is { } gate)
            await gate.WaitAsync(ct);
        ct.ThrowIfCancellationRequested();

        if (FailWith.TryGetValue(server.Id, out var failure))
            throw failure;

        var connection = new FakeMcpConnection(Tools.GetValueOrDefault(server.Id) ?? []);
        Connections.Add(connection);
        return connection;
    }
}

internal sealed class FakeMcpConnection(IReadOnlyList<McpCachedTool> tools) : IMcpConnection
{
    public bool IsAlive { get; set; } = true;

    public string RecentStderr => "";

    public int DisposeCount { get; private set; }

    public List<(string Name, string Arguments)> Calls { get; } = [];

    /// <summary>自訂呼叫的結果；沒有就回一段文字「ok」。</summary>
    public Func<string, JsonElement, McpCallResult>? OnCall { get; set; }

    public Task<IReadOnlyList<McpCachedTool>> ListToolsAsync(CancellationToken ct) => Task.FromResult(tools);

    public Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        Calls.Add((name, arguments.GetRawText()));
        var result = OnCall?.Invoke(name, arguments) ?? new McpCallResult(false, [new McpTextPart("ok")]);
        return Task.FromResult(result);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsAlive = false;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMcpToolCache : IMcpToolCache
{
    public Dictionary<string, IReadOnlyList<McpCachedTool>> Items { get; } = new(StringComparer.Ordinal);

    public List<string> Saved { get; } = [];

    public Task<IReadOnlyList<McpCachedTool>> GetAsync(string serverId, CancellationToken ct) =>
        Task.FromResult(Items.GetValueOrDefault(serverId) ?? []);

    public Task SaveAsync(string serverId, IReadOnlyList<McpCachedTool> tools, CancellationToken ct)
    {
        Items[serverId] = tools;
        Saved.Add(serverId);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string serverId, CancellationToken ct)
    {
        Items.Remove(serverId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeShellEnvironment : IShellEnvironment
{
    public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["PATH"] = "/usr/bin" });
}

internal sealed class FakeNotificationSink : INotificationSink
{
    public List<AppNotification> Shown { get; } = [];

    public Task ShowAsync(AppNotification notification, CancellationToken ct)
    {
        lock (Shown)
            Shown.Add(notification);
        return Task.CompletedTask;
    }
}

/// <summary>呼叫入口的假實作：記下呼叫，回傳指定的結果。</summary>
internal sealed class FakeMcpToolCaller : IMcpToolCaller
{
    public List<(string ServerId, string ToolName, string Arguments)> Calls { get; } = [];

    public Func<McpCallResult>? Result { get; set; }

    public Exception? Throw { get; set; }

    public Task<McpCallResult> CallAsync(string serverId, string toolName, string argumentsJson, CancellationToken ct)
    {
        Calls.Add((serverId, toolName, argumentsJson));
        if (Throw is { } ex)
            throw ex;
        return Task.FromResult(Result?.Invoke() ?? new McpCallResult(false, [new McpTextPart("ok")]));
    }
}

internal static class McpFixtures
{
    public static McpServerSettings Stdio(
        string id = "s1", string key = "github", string name = "GitHub", bool enabled = true, bool alsoInSprite = false,
        string secretsVersion = "v1", string command = "npx")
        => new(id, key, name, McpTransportKind.Stdio, command, ["-y", "server"], null, ["GITHUB_TOKEN"], null, [],
            enabled, alsoInSprite, 60, secretsVersion);

    public static McpServerSettings Http(string id = "h1", string key = "kb", string name = "KB", bool enabled = true)
        => new(id, key, name, McpTransportKind.Http, null, [], null, [], "https://kb.example.com/mcp", ["Authorization"],
            enabled, false, 60, "v1");

    public static McpCachedTool Tool(string name, string description = "Does a thing.", string? title = null, McpToolHints? hints = null,
        string schema = "{\"type\":\"object\",\"properties\":{}}")
        => new(name, title, description, schema, hints ?? new McpToolHints(null, null));

    public static ToolContext Context() => new("conv", ChatMode.Workbench, TimeProvider.System, null!);
}
