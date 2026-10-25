using System.IO.Pipelines;
using System.Text.Json;
using Lumi.Core.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lumi.Mcp.Tests;

/// <summary>用 SDK 的伺服器端與記憶體內的串流，驗證 <see cref="SdkMcpConnectionFactory.SdkMcpConnection"/> 的列工具與呼叫。</summary>
public sealed class SdkConnectionTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _serverCts = new();
    private Task _serverRun = Task.CompletedTask;
    private SdkMcpConnectionFactory.SdkMcpConnection _connection = null!;

    public async Task InitializeAsync()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        Func<string, string> echo = text => "echo: " + text;
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        tools.Add(McpServerTool.Create(echo, new McpServerToolCreateOptions { Name = "echo", Description = "回傳輸入的文字。" }));

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), "test-server", NullLoggerFactory.Instance),
            new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "test-server", Version = "1.0.0" },
                ToolCollection = tools,
            },
            NullLoggerFactory.Instance,
            null);
        _serverRun = server.RunAsync(_serverCts.Token);

        var transport = new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
        var client = await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "Lumi-test", Version = "1.0.0" } },
            NullLoggerFactory.Instance,
            CancellationToken.None);
        _connection = new SdkMcpConnectionFactory.SdkMcpConnection(client, new SdkMcpConnectionFactory.StderrTail());
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        _serverCts.Cancel();
        try
        {
            await _serverRun;
        }
        catch (Exception)
        {
            // 伺服器因為取消而結束
        }
        _serverCts.Dispose();
    }

    [Fact]
    public async Task Lists_the_tools_with_their_schemas()
    {
        var tools = await _connection.ListToolsAsync(default);

        var echo = Assert.Single(tools);
        Assert.Equal("echo", echo.Name);
        Assert.Equal("回傳輸入的文字。", echo.Description);
        Assert.Contains("object", echo.InputSchemaJson);
    }

    [Fact]
    public async Task Calls_a_tool_and_returns_its_text()
    {
        using var arguments = JsonDocument.Parse("{\"text\":\"hi\"}");

        var result = await _connection.CallToolAsync("echo", arguments.RootElement.Clone(), default);

        Assert.False(result.IsError);
        var text = Assert.IsType<McpTextPart>(Assert.Single(result.Parts));
        Assert.Equal("echo: hi", text.Text);
    }

    [Fact]
    public void Reports_alive_until_disposed()
    {
        Assert.True(_connection.IsAlive);
        Assert.Equal("", _connection.RecentStderr);
    }

    [Fact]
    public async Task A_missing_stdio_command_fails_as_a_connection_exception()
    {
        var factory = new SdkMcpConnectionFactory();
        var server = new McpServerSettings("s", "k", "n", McpTransportKind.Stdio, "/nonexistent/lumi-test-command-xyz", [], null, [], null, []);

        var ex = await Assert.ThrowsAsync<McpConnectionException>(() =>
            factory.ConnectAsync(server, McpServerSecrets.Empty, new Dictionary<string, string> { ["PATH"] = "/usr/bin" }, default));

        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Http_transport_uses_the_named_client_from_the_factory()
    {
        var handler = new RecordingHandler();
        var factory = new RecordingHttpClientFactory(handler);
        var server = new McpServerSettings("s", "k", "n", McpTransportKind.Http, null, [], null, [], "https://mcp.example.test/mcp", []);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SdkMcpConnectionFactory(factory).ConnectAsync(server, McpServerSecrets.Empty, new Dictionary<string, string>(), default));

        Assert.Equal([SdkMcpConnectionFactory.HttpClientName], factory.Names);
        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, uri => Assert.Equal("mcp.example.test", uri.Host));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            throw new HttpRequestException("測試用：不連線");
        }
    }

    private sealed class RecordingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
