using Lumi.Core.Secrets;

namespace Lumi.Core.Web;

public enum ProxyMode { System, Pac, Manual, None }

public enum ProxyAuthMode { None, Integrated, Account }

/// <summary>
/// 網路設定（M17）。Proxy 密碼不在這裡，只在 <see cref="ISecretStore"/>（<see cref="NetworkSecrets.ProxyPassword"/>）。
/// 清單一律去空白、去空行、轉小寫、去重、排序，這樣設定檔的內容穩定。
/// </summary>
public sealed record NetworkSettings
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 120;

    public static readonly NetworkSettings Default = new();

    public ProxyMode Mode { get; init; } = ProxyMode.System;

    /// <summary>Mode=Pac 時用：http、https 或 file 網址。</summary>
    public string PacUrl { get; init; } = "";

    /// <summary>Mode=Manual 時用：<c>host:port</c>、<c>http://host:port</c> 或 <c>socks5://host:port</c>。</summary>
    public string ManualProxy { get; init; } = "";

    /// <summary>Mode=Manual 時不經過 Proxy 的主機（<c>*.example.com</c>、<c>10.*</c>）；本機位址一律不經過。</summary>
    public IReadOnlyList<string> BypassList { get; init; } = [];

    public ProxyAuthMode Auth { get; init; } = ProxyAuthMode.Integrated;

    /// <summary>Auth=Account 時的帳號，可以寫成 <c>DOMAIN\user</c>。</summary>
    public string ProxyUser { get; init; } = "";

    /// <summary>網路工具連到這些網站時自動使用 Windows 登入身分（D8）。格式同 <see cref="BypassList"/>。</summary>
    public IReadOnlyList<string> IntegratedAuthSites { get; init; } = [];

    /// <summary>網路工具單次請求的逾時。</summary>
    public int TimeoutSeconds { get; init; } = DefaultTimeoutSeconds;

    public NetworkSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : ProxyMode.System,
        PacUrl = PacUrl?.Trim() ?? "",
        ManualProxy = ManualProxy?.Trim() ?? "",
        BypassList = Clean(BypassList),
        Auth = Enum.IsDefined(Auth) ? Auth : ProxyAuthMode.Integrated,
        ProxyUser = ProxyUser?.Trim() ?? "",
        IntegratedAuthSites = Clean(IntegratedAuthSites),
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds),
    };

    // record 預設的相等比較對清單是比參考；這裡改成比內容（IsDirty、設定變更偵測都靠它）
    public bool Equals(NetworkSettings? other) =>
        other is not null
        && Mode == other.Mode && PacUrl == other.PacUrl && ManualProxy == other.ManualProxy
        && Auth == other.Auth && ProxyUser == other.ProxyUser && TimeoutSeconds == other.TimeoutSeconds
        && (BypassList ?? []).SequenceEqual(other.BypassList ?? [])
        && (IntegratedAuthSites ?? []).SequenceEqual(other.IntegratedAuthSites ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Mode);
        hash.Add(PacUrl);
        hash.Add(ManualProxy);
        hash.Add(Auth);
        hash.Add(ProxyUser);
        hash.Add(TimeoutSeconds);
        foreach (var item in BypassList ?? [])
            hash.Add(item);
        foreach (var item in IntegratedAuthSites ?? [])
            hash.Add(item);
        return hash.ToHashCode();
    }

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? lines) =>
        (lines ?? [])
            .Select(l => l?.Trim().ToLowerInvariant() ?? "")
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
}

/// <summary>Proxy 密碼在 <see cref="ISecretStore"/> 裡的名稱（同 <c>SpeechSecrets</c> 的格式）。</summary>
public static class NetworkSecrets
{
    public static readonly SecretKey ProxyPassword = new("network/proxy/password");
}
