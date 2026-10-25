using System.ComponentModel;
using System.Net;
using System.Net.Http;
using ModelContextProtocol.Client;
using Lumi.Core.Mcp;

namespace Lumi.Mcp;

/// <summary>工具呼叫失敗。訊息是給使用者與模型看的繁中文字，已去掉祕密的值。</summary>
public sealed class McpCallException(string message) : Exception(message);

/// <summary>連線失敗（工廠丟出）。內層例外可能含 SDK 細節，<see cref="StandardError"/> 是 stdio 最後幾行 stderr。</summary>
public sealed class McpConnectionException(Exception inner, IReadOnlyList<string> standardError) : Exception("MCP 連線失敗。", inner)
{
    public IReadOnlyList<string> StandardError { get; } = standardError;
}

/// <summary>管理員內部用的連線失敗：訊息已經是安全的文字。</summary>
internal sealed class McpFailure(string message) : Exception(message);

/// <summary>把例外轉成給使用者看的文字（M16 16C：不得包含祕密的值）。</summary>
internal static class McpFailureText
{
    private const string Redacted = "***";

    /// <summary>連線失敗的原因＋stdio 的 stderr（換掉所有祕密的值）。</summary>
    public static string DescribeConnection(Exception ex, McpServerSettings server, IEnumerable<string> secretValues)
    {
        var stderr = (ex as McpConnectionException)?.StandardError ?? [];
        var inner = ex is McpConnectionException { InnerException: { } cause } ? cause : ex;
        var reason = ConnectionReason(inner, server);
        var text = stderr.Count == 0 ? reason : reason + "\n" + string.Join("\n", stderr);
        return Redact(text, secretValues);
    }

    /// <summary>工具呼叫失敗：只說類型，不附例外訊息（訊息可能含引數或祕密）。</summary>
    public static string DescribeCall(Exception ex) => $"呼叫工具失敗（{ex.GetType().Name}）。";

    public static string Redact(string text, IEnumerable<string> secretValues)
    {
        foreach (var value in secretValues)
        {
            if (!string.IsNullOrEmpty(value))
                text = text.Replace(value, Redacted, StringComparison.Ordinal);
        }
        return text;
    }

    private static string ConnectionReason(Exception ex, McpServerSettings server)
    {
        if (Any(ex, e => e is Win32Exception or FileNotFoundException or DirectoryNotFoundException))
            return $"找不到指令「{server.Command}」";
        if (Any(ex, e => e is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }))
            return "伺服器拒絕驗證，請檢查標頭";
        if (Any(ex, e => e is HttpRequestException))
            return "網路連線失敗";
        if (Any(ex, e => e is ClientTransportClosedException))
            return "伺服器提前結束連線";
        return $"無法完成連線（{ex.GetType().Name}）";
    }

    private static bool Any(Exception ex, Func<Exception, bool> match)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (match(e))
                return true;
        }
        return false;
    }
}
