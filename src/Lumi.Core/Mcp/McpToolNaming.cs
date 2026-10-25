using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Core.Mcp;

/// <summary>MCP 工具名稱與伺服器代號的規則（M16 D8）。</summary>
public static partial class McpToolNaming
{
    public const int MaxLength = 64;
    public const int MaxKeyLength = 16;
    private const int HashLength = 8;

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,14}[a-z0-9])?\z")]
    private static partial Regex ValidKey();

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex InvalidNameChars();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonKeyChars();

    /// <summary>
    /// <c>mcp__{key}__{tool}</c>：不在 [A-Za-z0-9_-] 的字元換成 _；超過 64 時取前 55 字元加 "_" 加原始完整名稱 SHA-256 的前 8 個十六進位（小寫）。
    /// </summary>
    public static string ToolName(string serverKey, string toolName)
    {
        var raw = $"mcp__{serverKey}__{toolName}";
        var name = InvalidNameChars().Replace(raw, "_");
        if (name.Length <= MaxLength)
            return name;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant()[..HashLength];
        return name[..(MaxLength - HashLength - 1)] + "_" + hash;
    }

    public static bool IsValidKey(string? key) => key is not null && ValidKey().IsMatch(key);

    /// <summary>
    /// 由名稱產生代號：轉小寫、非英數換成 -、合併連續 -、去頭尾 -、最多 16；結果空白時用 "server"；
    /// 和 <paramref name="existingKeys"/> 重複時接 -2、-3…（總長度仍不超過 16）。
    /// </summary>
    public static string KeyFromName(string name, IEnumerable<string> existingKeys)
    {
        var taken = new HashSet<string>(existingKeys, StringComparer.Ordinal);
        var key = NonKeyChars().Replace((name ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        if (key.Length > MaxKeyLength)
            key = key[..MaxKeyLength].TrimEnd('-');
        if (key.Length == 0)
            key = "server";

        if (!taken.Contains(key))
            return key;

        for (var n = 2; ; n++)
        {
            var suffix = "-" + n;
            var candidate = key[..Math.Min(key.Length, MaxKeyLength - suffix.Length)].TrimEnd('-') + suffix;
            if (!taken.Contains(candidate))
                return candidate;
        }
    }
}
