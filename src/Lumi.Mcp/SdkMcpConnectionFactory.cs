using System.Text.Json;
using Lumi.Core.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Lumi.Mcp;

/// <summary>
/// 用官方 C# SDK（ModelContextProtocol.Core）實作的連線工廠（M16 16C）。SDK 的型別只允許出現在這個檔案
/// （架構測試 <c>McpLibraryRuleTests</c> 守住相依方向）。祕密的值只放進程序環境變數與 HTTP 標頭，不寫 log、不進例外訊息。
/// </summary>
public sealed class SdkMcpConnectionFactory(IHttpClientFactory? httpClientFactory = null) : IMcpConnectionFactory
{
    /// <summary>遠端 MCP 伺服器用的具名 HttpClient；它和其他 HttpClient 一樣經過 Lumi 的 Proxy 設定（M17 D2）。</summary>
    public const string HttpClientName = "mcp";

    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private static readonly string ClientVersion =
        typeof(SdkMcpConnectionFactory).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public async Task<IMcpConnection> ConnectAsync(McpServerSettings server, McpServerSecrets secrets, IReadOnlyDictionary<string, string> baseEnvironment, CancellationToken ct)
    {
        var stderr = new StderrTail();
        var transport = server.Transport switch
        {
            McpTransportKind.Stdio => CreateStdioTransport(server, secrets, baseEnvironment, stderr),
            McpTransportKind.Http => CreateHttpTransport(server, secrets, httpClientFactory),
            _ => throw new ArgumentOutOfRangeException(nameof(server), server.Transport, "不認得的傳輸方式。"),
        };

        try
        {
            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "Lumi", Version = ClientVersion },
            };
            var client = await McpClient.CreateAsync(transport, options, NullLoggerFactory.Instance, ct);
            return new SdkMcpConnection(client, stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeTransportAsync(transport);
            throw;
        }
        catch (Exception ex)
        {
            await DisposeTransportAsync(transport);
            // 例外訊息不帶進來：內層可能含祕密；由管理員依類型說明原因
            throw new McpConnectionException(ex, stderr.Lines);
        }
    }

    private static IClientTransport CreateStdioTransport(McpServerSettings server, McpServerSecrets secrets, IReadOnlyDictionary<string, string> baseEnvironment, StderrTail stderr)
    {
        if (string.IsNullOrWhiteSpace(server.Command))
            throw new ArgumentException("缺少指令。", nameof(server));

        // 基底環境（登入 shell 的 PATH 等）再蓋上祕密裡的環境變數
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in baseEnvironment)
            environment[name] = value;
        foreach (var (name, value) in secrets.Env)
            environment[name] = value;

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = ResolveCommand(server.Command, baseEnvironment),
            Arguments = server.Args.ToList(),
            WorkingDirectory = string.IsNullOrWhiteSpace(server.WorkingDirectory) ? null : server.WorkingDirectory,
            EnvironmentVariables = environment,
            StandardErrorLines = stderr.Add,
        });
    }

    private static IClientTransport CreateHttpTransport(McpServerSettings server, McpServerSecrets secrets, IHttpClientFactory? httpClientFactory)
    {
        if (string.IsNullOrWhiteSpace(server.Url))
            throw new ArgumentException("缺少網址。", nameof(server));

        var headers = new Dictionary<string, string>(secrets.Headers, StringComparer.OrdinalIgnoreCase);
        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
        };
        if (httpClientFactory is null)
            return new HttpClientTransport(options, NullLoggerFactory.Instance);

        // 串流回應會持續很久，逾時靠 CancellationToken 與每個伺服器自己的逾時設定，不靠 HttpClient 的整體逾時
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.Timeout = Timeout.InfiniteTimeSpan;
        return new HttpClientTransport(options, http, NullLoggerFactory.Instance, ownsHttpClient: false);
    }

    /// <summary>Windows 上 <c>npx</c> 實際是 <c>npx.cmd</c>，找得到就用完整路徑；其他平台原樣使用。</summary>
    private static string ResolveCommand(string command, IReadOnlyDictionary<string, string> baseEnvironment)
    {
        if (!OperatingSystem.IsWindows())
            return command;

        var path = Lookup(baseEnvironment, "PATH") ?? "";
        var pathExt = Lookup(baseEnvironment, "PATHEXT") ?? DefaultPathExt;
        return ResolveWindowsCommand(command, path, pathExt, File.Exists) ?? command;
    }

    /// <summary>
    /// 在 <paramref name="path"/> 的每個資料夾裡，依 <paramref name="pathExt"/> 找出存在的檔案。指令已有副檔名、或找不到時回 null。
    /// 指令含路徑分隔符時只看那個位置。
    /// </summary>
    internal static string? ResolveWindowsCommand(string command, string path, string pathExt, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command) || Path.HasExtension(command))
            return null;

        var extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var directories = command.Contains('\\') || command.Contains('/')
            ? [""]
            : path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                var candidate = directory.Length == 0 ? command + extension : Path.Combine(directory, command + extension);
                if (fileExists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> environment, string name)
    {
        foreach (var (key, value) in environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    private static async Task DisposeTransportAsync(IClientTransport transport)
    {
        if (transport is IAsyncDisposable disposable)
        {
            try
            {
                await disposable.DisposeAsync();
            }
            catch (Exception)
            {
                // 清理失敗不影響結果（程序可能已經結束）
            }
        }
    }

    /// <summary>SDK 的 <see cref="McpClient"/> 包成 <see cref="IMcpConnection"/>（internal 讓測試可以接記憶體內的伺服器）。</summary>
    internal sealed class SdkMcpConnection(McpClient client, StderrTail stderr) : IMcpConnection
    {
        public bool IsAlive => !client.Completion.IsCompleted;

        public string RecentStderr => stderr.Text;

        public async Task<IReadOnlyList<McpCachedTool>> ListToolsAsync(CancellationToken ct)
        {
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return tools.Select(t => ToCached(t.ProtocolTool)).ToList();
        }

        public async Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in arguments.EnumerateObject())
                    values[property.Name] = property.Value.Clone();
            }

            var result = await client.CallToolAsync(name, values, cancellationToken: ct);
            return new McpCallResult(result.IsError ?? false, result.Content.Select(ToPart).ToList());
        }

        public async ValueTask DisposeAsync() => await client.DisposeAsync();

        private static McpCachedTool ToCached(Tool tool) => new(
            tool.Name,
            string.IsNullOrWhiteSpace(tool.Title) ? tool.Annotations?.Title : tool.Title,
            tool.Description ?? "",
            tool.InputSchema.ValueKind == JsonValueKind.Undefined ? "{}" : tool.InputSchema.GetRawText(),
            new McpToolHints(tool.Annotations?.ReadOnlyHint, tool.Annotations?.DestructiveHint));

        private static McpContentPart ToPart(ContentBlock block) => block switch
        {
            TextContentBlock text => new McpTextPart(text.Text),
            ImageContentBlock image => new McpImagePart(image.MimeType ?? "application/octet-stream", image.DecodedData.Length),
            EmbeddedResourceBlock { Resource: TextResourceContents text } => new McpResourcePart(text.Uri, text.Text),
            EmbeddedResourceBlock embedded => new McpResourcePart(embedded.Resource.Uri, null),
            ResourceLinkBlock link => new McpResourcePart(link.Uri, null),
            _ => new McpTextPart($"[未支援的內容類型：{block.Type}]"),
        };
    }

    /// <summary>stdio 最後 5 行 stderr，每行最多 200 字元。</summary>
    internal sealed class StderrTail
    {
        private const int MaxLines = 5;
        private const int MaxLength = 200;

        private readonly object _lock = new();
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            var text = line.Length > MaxLength ? line[..MaxLength] : line;
            lock (_lock)
            {
                _lines.Enqueue(text);
                while (_lines.Count > MaxLines)
                    _lines.Dequeue();
            }
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lock)
                {
                    return _lines.ToList();
                }
            }
        }

        public string Text => string.Join("\n", Lines);
    }
}
