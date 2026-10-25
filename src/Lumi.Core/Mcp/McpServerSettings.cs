using Lumi.Core.Secrets;

namespace Lumi.Core.Mcp;

public enum McpTransportKind { Stdio, Http }

/// <summary>
/// 一個 MCP 伺服器（M16）。祕密（環境變數與標頭的值）不在這裡，只在 <see cref="Secrets.ISecretStore"/>（<see cref="McpSecrets.KeyFor"/>）。
/// </summary>
/// <param name="Key">工具名稱用的代號：小寫英數與 -，1～16 字元，不可重複（<see cref="McpToolNaming"/>）。</param>
/// <param name="EnvNames">環境變數名稱（值在祕密裡）。</param>
/// <param name="HeaderNames">HTTP 標頭名稱（值在祕密裡）。</param>
/// <param name="SecretsVersion">每次寫入祕密就換一個新值，讓管理員知道要重新連線。</param>
public sealed record McpServerSettings(
    string Id,
    string Key,
    string Name,
    McpTransportKind Transport,
    string? Command,
    IReadOnlyList<string> Args,
    string? WorkingDirectory,
    IReadOnlyList<string> EnvNames,
    string? Url,
    IReadOnlyList<string> HeaderNames,
    bool Enabled = true,
    bool AlsoInSprite = false,
    int TimeoutSeconds = 60,
    string? SecretsVersion = null)
{
    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>環境變數與標頭的值。只在記憶體與 <see cref="Secrets.ISecretStore"/> 之間傳遞，不寫 log、不寫進例外訊息。</summary>
public sealed record McpServerSecrets(IReadOnlyDictionary<string, string> Env, IReadOnlyDictionary<string, string> Headers)
{
    public static readonly McpServerSecrets Empty = new(new Dictionary<string, string>(), new Dictionary<string, string>());
}

public static class McpSecrets
{
    /// <summary>每個伺服器一個祕密，內容是 <see cref="McpServerSecrets"/> 的 JSON。</summary>
    public static SecretKey KeyFor(string serverId) => new($"mcp/{serverId}/secrets");
}
