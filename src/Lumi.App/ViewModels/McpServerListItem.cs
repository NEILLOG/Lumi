using System.Globalization;
using Lumi.App.Resources;
using Lumi.Core.Mcp;

namespace Lumi.App.ViewModels;

/// <summary>MCP 伺服器清單的一列（卡片）：字首方塊、名稱、副標（「本機指令 · npx」或「遠端 · kb.corp」）、狀態（文字加圓點）。</summary>
public sealed record McpServerListItem(string Id, string Name, McpTransportKind Transport, string? Command, string? Url,
    McpServerState State, int ToolCount, string? Error)
{
    /// <summary>字首方塊的字元：名稱的第一個字（英文轉大寫）；名稱是空的就用「M」。</summary>
    public string Initial => InitialOf(Name);

    /// <summary>名稱的字首（編輯區標頭也用）。</summary>
    public static string InitialOf(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? StringInfo.GetNextTextElement(trimmed).ToUpperInvariant() : "M";
    }

    /// <summary>副標：stdio 是「本機指令 · 指令的檔名」，HTTP 是「遠端 · 主機」。</summary>
    public string Subtitle => Transport == McpTransportKind.Stdio
        ? string.Format(CultureInfo.CurrentCulture, Strings.Mcp_SubtitleStdio, CommandFileName)
        : string.Format(CultureInfo.CurrentCulture, Strings.Mcp_SubtitleHttp, HostName);

    public string StatusText => State switch
    {
        McpServerState.Ready => string.Format(CultureInfo.CurrentCulture, Strings.Mcp_StatusReady, ToolCount),
        McpServerState.Connecting => Strings.Mcp_StatusConnecting,
        McpServerState.Failed => Strings.Mcp_StatusFailed,
        _ => Strings.Mcp_StatusDisabled,
    };

    /// <summary>卡片的可存取名稱（螢幕閱讀器）：「GitHub，本機指令 · npx，已連線 · 12 個工具」。</summary>
    public string AutomationName => $"{Name}，{Subtitle}，{StatusText}";

    public bool IsReady => State == McpServerState.Ready;

    public bool IsConnecting => State == McpServerState.Connecting;

    public bool IsFailed => State == McpServerState.Failed;

    public bool IsDisabled => State == McpServerState.Disabled;

    private string CommandFileName => Command is { Length: > 0 } c ? Path.GetFileName(c) : "";

    private string HostName => Url is not null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url ?? "";
}
