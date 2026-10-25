using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Reminders;
using Lumi.Core.Tools;
using Lumi.Mcp.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Mcp.Tests;

public sealed class McpServerManagerTests : IAsyncLifetime
{
    private FakeMcpModelCatalog _catalog = null!;
    private FakeMcpConnectionFactory _factory = null!;
    private FakeMcpToolCache _cache = null!;
    private FakeNotificationSink _notifications = null!;
    private McpServerManager _manager = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_manager is not null)
            await _manager.DisposeAsync();
    }

    private McpServerManager Create(params McpServerSettings[] servers)
    {
        _catalog = new FakeMcpModelCatalog(servers);
        _factory = new FakeMcpConnectionFactory();
        _cache = new FakeMcpToolCache();
        _notifications = new FakeNotificationSink();
        _manager = new McpServerManager(_catalog, _factory, _cache, new FakeShellEnvironment(), _notifications,
            TimeProvider.System, NullLogger<McpServerManager>.Instance);
        return _manager;
    }

    private McpServerStatus Status(string serverId) => _manager.GetStatuses().Single(s => s.ServerId == serverId);

    [Fact]
    public async Task Start_connects_becomes_ready_and_saves_the_tool_cache()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search"), McpFixtures.Tool("get_issue")];

        await manager.StartAllAsync();

        var status = Status("s1");
        Assert.Equal(McpServerState.Ready, status.State);
        Assert.Equal(2, status.ToolCount);
        Assert.Contains("s1", _cache.Saved);
        Assert.Equal(2, _manager.GetTools().Count);
        Assert.All(_manager.GetTools(), t => Assert.True(t.IsLive));
    }

    [Fact]
    public async Task Failed_start_becomes_failed_and_sends_one_notice()
    {
        var manager = Create(McpFixtures.Stdio(command: "npx-missing"));
        _factory.FailWith["s1"] = new McpConnectionException(new Win32Exception(2), []);

        await manager.StartAllAsync();

        var status = Status("s1");
        Assert.Equal(McpServerState.Failed, status.State);
        Assert.Equal("找不到指令「npx-missing」", status.Error);
        var notice = Assert.Single(_notifications.Shown);
        Assert.Equal("mcp-s1", notice.Id);
        Assert.Equal(NotificationKind.SystemNotice, notice.Kind);
        Assert.Contains("GitHub", notice.Message);
    }

    [Fact]
    public async Task Disabled_server_is_not_connected()
    {
        var manager = Create(McpFixtures.Stdio(enabled: false));

        await manager.StartAllAsync();

        Assert.Empty(_factory.Attempts);
        Assert.Equal(McpServerState.Disabled, Status("s1").State);
        Assert.Empty(manager.GetTools());
    }

    [Fact]
    public async Task Changing_the_secrets_version_reconnects_but_renaming_does_not()
    {
        var manager = Create(McpFixtures.Stdio());
        await manager.StartAllAsync();
        Assert.Single(_factory.Attempts);

        _catalog.SetServers(McpFixtures.Stdio(name: "GitHub 2"));
        await WaitForIdle(manager);
        Assert.Single(_factory.Attempts);

        _catalog.SetServers(McpFixtures.Stdio(name: "GitHub 2", secretsVersion: "v2"));
        await WaitForIdle(manager);
        Assert.Equal(2, _factory.Attempts.Count);
        Assert.Equal(McpServerState.Ready, Status("s1").State);
    }

    [Fact]
    public async Task Removing_the_server_disconnects_it_and_deletes_its_cache()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search")];
        await manager.StartAllAsync();
        var connection = _factory.Connections.Single();

        _catalog.SetServers();

        Assert.Empty(manager.GetStatuses());
        Assert.Empty(manager.GetTools());
        await WaitUntil(() => connection.DisposeCount == 1);
        await WaitUntil(() => !_cache.Items.ContainsKey("s1"));
    }

    [Fact]
    public async Task Offline_server_shows_its_cached_tools_as_not_live()
    {
        var manager = Create(McpFixtures.Stdio());
        _cache.Items["s1"] = [McpFixtures.Tool("search")];
        _factory.FailWith["s1"] = new Exception("offline");

        await manager.StartAllAsync();

        var tool = Assert.Single(manager.GetTools());
        Assert.False(tool.IsLive);
        Assert.Equal(McpServerState.Failed, Status("s1").State);
        Assert.Equal(1, Status("s1").ToolCount);
    }

    [Fact]
    public async Task Call_reconnects_once_when_the_connection_has_died()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search")];
        await manager.StartAllAsync();
        _factory.Connections.Single().IsAlive = false;

        var result = await manager.CallAsync("s1", "search", "{\"q\":\"x\"}", default);

        Assert.Equal(2, _factory.Attempts.Count);
        Assert.False(result.IsError);
        Assert.Equal("search", _factory.Connections.Last().Calls.Single().Name);
    }

    [Fact]
    public async Task Call_throws_a_fixed_message_when_the_reconnect_fails()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search")];
        await manager.StartAllAsync();
        _factory.Connections.Single().IsAlive = false;
        _factory.FailWith["s1"] = new Exception("boom");

        var ex = await Assert.ThrowsAsync<McpCallException>(() => manager.CallAsync("s1", "search", "{}", default));

        Assert.Equal("MCP 伺服器「GitHub」目前沒有連線。", ex.Message);
        Assert.Equal(McpServerState.Failed, Status("s1").State);
    }

    [Fact]
    public async Task Call_to_an_unknown_server_throws()
    {
        var manager = Create();

        await Assert.ThrowsAsync<McpCallException>(() => manager.CallAsync("nope", "search", "{}", default));
    }

    [Fact]
    public async Task WaitForStartup_returns_at_the_timeout_while_still_connecting()
    {
        var manager = Create(McpFixtures.Stdio());
        var release = new TaskCompletionSource();
        _factory.Gate = release.Task;

        var starting = manager.StartAllAsync();
        Assert.Equal(McpServerState.Connecting, Status("s1").State);

        await manager.WaitForStartupAsync(TimeSpan.FromMilliseconds(100), default);

        Assert.Equal(McpServerState.Connecting, Status("s1").State);
        release.SetResult();
        await starting;
        Assert.Equal(McpServerState.Ready, Status("s1").State);
    }

    [Fact]
    public async Task WaitForStartup_returns_as_soon_as_the_connections_finish()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search")];
        var release = new TaskCompletionSource();
        _factory.Gate = release.Task;
        var starting = manager.StartAllAsync();

        var waiting = manager.WaitForStartupAsync(TimeSpan.FromSeconds(20), default);
        release.SetResult();
        await waiting;

        Assert.Equal(McpServerState.Ready, Status("s1").State);
        await starting;
    }

    [Fact]
    public async Task Test_connection_does_not_change_the_running_connection()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search")];
        await manager.StartAllAsync();
        var running = _factory.Connections.Single();

        var result = await manager.TestAsync(McpFixtures.Stdio(), McpServerSecrets.Empty, default);

        Assert.True(result.Success);
        Assert.Single(result.ToolNames);
        Assert.True(running.IsAlive);
        Assert.Equal(McpServerState.Ready, Status("s1").State);
        Assert.Equal(1, _factory.Connections.Last().DisposeCount);
    }

    [Fact]
    public async Task Test_connection_reports_failure_without_secret_values()
    {
        var manager = Create();
        _catalog.Secrets["s1"] = new McpServerSecrets(
            new Dictionary<string, string> { ["GITHUB_TOKEN"] = "TOPSECRET-42" },
            new Dictionary<string, string>());
        _factory.FailWith["s1"] = new McpConnectionException(
            new Exception("failed with TOPSECRET-42"),
            ["stderr echoed TOPSECRET-42"]);

        var result = await manager.TestAsync(McpFixtures.Stdio(), _catalog.Secrets["s1"], default);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("TOPSECRET-42", result.Error);
        Assert.Contains("***", result.Error);
    }

    [Fact]
    public async Task Connection_errors_never_contain_secret_values()
    {
        var manager = Create(McpFixtures.Http());
        _catalog.Secrets["h1"] = new McpServerSecrets(
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Authorization"] = "Bearer TOPSECRET-99" });
        _factory.FailWith["h1"] = new McpConnectionException(
            new HttpRequestException("rejected Bearer TOPSECRET-99", null, HttpStatusCode.Unauthorized),
            []);

        await manager.StartAllAsync();

        var error = Status("h1").Error;
        Assert.Equal("伺服器拒絕驗證，請檢查標頭", error);
        Assert.DoesNotContain("TOPSECRET-99", error);
    }

    [Fact]
    public async Task Changing_the_tool_permission_is_visible_without_reconnecting()
    {
        var manager = Create(McpFixtures.Stdio());
        _factory.Tools["s1"] = [McpFixtures.Tool("search")];
        await manager.StartAllAsync();

        _catalog.SetTools(new ToolSettings
        {
            PermissionOverrides = new Dictionary<string, ToolPermission> { ["mcp__github__search"] = ToolPermission.ReadOnly },
        });

        var tool = Assert.Single(manager.GetTools());
        Assert.Equal(ToolPermission.ReadOnly, tool.Descriptor.Permission);
        Assert.Single(_factory.Attempts);
    }

    private static async Task WaitForIdle(McpServerManager manager)
    {
        await manager.WaitForStartupAsync(TimeSpan.FromSeconds(10), default);
        await WaitUntil(() => manager.GetStatuses().All(s => s.State != McpServerState.Connecting));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition(), "等待條件逾時");
    }
}
