using System.Text.Json;
using Lumi.Core.Mcp;

namespace Lumi.Mcp;

/// <summary>一條已連上的 MCP 連線（M16 16C）。SDK 的型別只出現在 <see cref="SdkMcpConnectionFactory"/> 裡，其他檔案只看這個介面。</summary>
public interface IMcpConnection : IAsyncDisposable
{
    /// <summary>列出伺服器的工具（同時給快取與 Adapter 使用）。</summary>
    Task<IReadOnlyList<McpCachedTool>> ListToolsAsync(CancellationToken ct);

    /// <summary>呼叫工具。<paramref name="arguments"/> 是模型給的引數；不是 JSON 物件時視為沒有引數。</summary>
    Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct);

    /// <summary>連線還在不在（stdio 程序沒結束、HTTP 工作階段沒斷）。</summary>
    bool IsAlive { get; }

    /// <summary>stdio 最後 5 行 stderr（每行最多 200 字元）；其他傳輸是空字串。</summary>
    string RecentStderr { get; }
}

/// <summary>建立連線。<paramref name="baseEnvironment"/> 是啟動 stdio 程序用的環境變數（M16 D14）；祕密的值只用來設定環境變數與標頭。</summary>
public interface IMcpConnectionFactory
{
    Task<IMcpConnection> ConnectAsync(McpServerSettings server, McpServerSecrets secrets, IReadOnlyDictionary<string, string> baseEnvironment, CancellationToken ct);
}

/// <summary>工具呼叫的結果（SDK 的型別已轉成 Lumi 的型別）。</summary>
public sealed record McpCallResult(bool IsError, IReadOnlyList<McpContentPart> Parts);

/// <summary>結果裡的一個內容片段（M16 D10）。</summary>
public abstract record McpContentPart;

public sealed record McpTextPart(string Text) : McpContentPart;

/// <summary>圖片目前不交給模型，只記下種類與大小（給一行說明用）。</summary>
public sealed record McpImagePart(string MimeType, int Bytes) : McpContentPart;

/// <summary>資源：嵌入的文字資源有 <see cref="Text"/>；其他只有 URI。</summary>
public sealed record McpResourcePart(string Uri, string? Text) : McpContentPart;

/// <summary>管理員給 <see cref="McpTool"/> 用的呼叫入口（由 <see cref="McpServerManager"/> 實作）。</summary>
internal interface IMcpToolCaller
{
    Task<McpCallResult> CallAsync(string serverId, string toolName, string argumentsJson, CancellationToken ct);
}
