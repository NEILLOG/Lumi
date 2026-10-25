using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumi.Core.Web;

/// <summary>
/// 網路設定的欄位驗證與主機比對（M17，純函式，設定頁與 <c>IModelCatalog.SetNetworkAsync</c> 都用它）。
/// 驗證方法回傳可以直接顯示的錯誤文字；沒有問題時回 null。
/// </summary>
public static partial class NetworkRules
{
    public static string? ValidatePacUrl(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
            return "請輸入 PAC 網址";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "file"))
            return "請輸入 http、https 或 file 開頭的完整網址";
        return null;
    }

    public static string? ValidateManualProxy(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
            return "請輸入 Proxy 位址";
        if (SplitAuthority(AddDefaultScheme(value)) is { HasUserInfo: true })
            return "位址不可包含帳號密碼，請改用下方的驗證設定";
        if (!TryParseManualProxy(value, out _))
            return "請輸入主機與連接埠，例如 proxy.example.com:8080";
        return null;
    }

    /// <summary>
    /// 解析手動 Proxy：沒有 <c>://</c> 時補 <c>http://</c>；scheme 只能是 http、socks5、socks4；
    /// 一定要寫出 port（1～65535）；路徑只能是空或 <c>/</c>；不可含帳號密碼。
    /// </summary>
    public static bool TryParseManualProxy(string text, out Uri proxy)
    {
        proxy = null!;
        var value = AddDefaultScheme(text?.Trim() ?? "");
        if (SplitAuthority(value) is not { HasUserInfo: false } parts)
            return false;
        if (parts.Scheme is not ("http" or "socks5" or "socks4"))
            return false;
        if (parts.Host.Length == 0 || parts.Port is null or < 1 or > 65535)
            return false;
        if (parts.Rest is not ("" or "/"))
            return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return false;

        proxy = uri;
        return true;
    }

    /// <summary>一行主機樣式：<c>*.example.com</c>、<c>proxy.corp</c>、<c>10.*</c>、<c>192.168.1.*</c>、<c>10.0.0.5</c>。</summary>
    public static string? ValidateHostPattern(string line)
    {
        var value = line?.Trim().ToLowerInvariant() ?? "";
        return IsValidHostPattern(value) ? null : $"「{line?.Trim()}」不是有效的主機名稱";
    }

    /// <summary>回第一個錯誤；全部都有效時回 null。</summary>
    public static string? ValidateHostPatterns(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (ValidateHostPattern(line) is { } error)
                return error;
        }
        return null;
    }

    /// <summary>
    /// 不分大小寫比對。<c>*.example.com</c> 符合 <c>example.com</c> 與它的所有子網域；
    /// IPv4 樣式逐段比對，<c>*</c> 符合任何一段，結尾的 <c>*</c> 可以代表剩下的所有段（<c>10.*</c> 符合 <c>10.1.2.3</c>）；其他是完全相同。
    /// </summary>
    public static bool HostMatches(string host, IReadOnlyList<string> patterns)
    {
        var target = (host ?? "").Trim().ToLowerInvariant();
        if (target.Length == 0)
            return false;

        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim().ToLowerInvariant() ?? "";
            if (pattern.Length == 0)
                continue;

            if (IsIpv4Pattern(pattern))
            {
                if (Ipv4Matches(target, pattern))
                    return true;
            }
            else if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[2..];
                if (target == suffix || target.EndsWith("." + suffix, StringComparison.Ordinal))
                    return true;
            }
            else if (target == pattern)
            {
                return true;
            }
        }
        return false;
    }

    public static string? ValidateProxyUser(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "請輸入帳號" : null;

    public static string? ValidateTimeout(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds is >= NetworkSettings.MinTimeoutSeconds and <= NetworkSettings.MaxTimeoutSeconds
            ? null
            : "請輸入 5 到 120 之間的整數";

    public static string? ValidateTestUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
            ? null
            : "請輸入 http 或 https 開頭的完整網址";

    /// <summary>依換行切開、去頭尾空白、去空行。</summary>
    public static IReadOnlyList<string> ParseLines(string? multiline) =>
        (multiline ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    /// <summary>依 Proxy 模式檢查需要的欄位，再檢查網站清單與逾時；回第一個錯誤或 null。</summary>
    public static string? Validate(NetworkSettings s)
    {
        switch (s.Mode)
        {
            case ProxyMode.Pac:
                if (ValidatePacUrl(s.PacUrl) is { } pacError)
                    return pacError;
                break;
            case ProxyMode.Manual:
                if (ValidateManualProxy(s.ManualProxy) is { } proxyError)
                    return proxyError;
                if (ValidateHostPatterns(s.BypassList) is { } bypassError)
                    return bypassError;
                break;
        }

        if (s.Auth == ProxyAuthMode.Account && s.Mode != ProxyMode.None && ValidateProxyUser(s.ProxyUser) is { } userError)
            return userError;

        if (ValidateHostPatterns(s.IntegratedAuthSites) is { } sitesError)
            return sitesError;

        return ValidateTimeout(s.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
    }

    // ---- 內部 ----

    private static string AddDefaultScheme(string text) =>
        text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text;

    private sealed record AuthorityParts(string Scheme, bool HasUserInfo, string Host, int? Port, string Rest);

    /// <summary>把 <c>scheme://[userinfo@]host[:port]rest</c> 拆開（不依賴 <see cref="Uri"/>，這樣才分得出 port 有沒有寫出來）。</summary>
    private static AuthorityParts? SplitAuthority(string text)
    {
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return null;

        var scheme = text[..schemeEnd].ToLowerInvariant();
        var afterScheme = text[(schemeEnd + 3)..];
        var authorityEnd = afterScheme.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? afterScheme : afterScheme[..authorityEnd];
        var rest = authorityEnd < 0 ? "" : afterScheme[authorityEnd..];

        var hasUserInfo = authority.Contains('@');
        if (hasUserInfo)
            authority = authority[(authority.LastIndexOf('@') + 1)..];

        string host;
        string portText;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0)
                return new AuthorityParts(scheme, hasUserInfo, "", null, rest);
            host = authority[1..close];
            var after = authority[(close + 1)..];
            portText = after.StartsWith(':') ? after[1..] : "";
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            portText = colon < 0 ? "" : authority[(colon + 1)..];
        }

        int? port = int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : null;
        return new AuthorityParts(scheme, hasUserInfo, host, port, rest);
    }

    private static bool IsValidHostPattern(string value)
    {
        if (value.Length == 0)
            return false;

        var segments = value.Split('.');
        if (segments.All(IsIpv4Segment))
        {
            // IPv4 樣式：四段，或少於四段但以 * 結尾（10.*）
            if (segments.Any(s => s != "*" && int.Parse(s, CultureInfo.InvariantCulture) > 255))
                return false;
            return segments.Length == 4 || (segments.Length is >= 2 and < 4 && segments[^1] == "*");
        }

        var name = value.StartsWith("*.", StringComparison.Ordinal) ? value[2..] : value;
        return HostNamePattern().IsMatch(name);
    }

    private static bool IsIpv4Segment(string segment) =>
        segment == "*" || (segment.Length is >= 1 and <= 3 && segment.All(char.IsAsciiDigit));

    private static bool IsIpv4Pattern(string pattern)
    {
        var segments = pattern.Split('.');
        return segments.All(IsIpv4Segment) && (segments.Length == 4 || (segments.Length < 4 && segments[^1] == "*"));
    }

    private static bool Ipv4Matches(string host, string pattern)
    {
        var hostSegments = host.Split('.');
        if (hostSegments.Length != 4 || !hostSegments.All(s => IsIpv4Segment(s) && s != "*"))
            return false;

        var patternSegments = pattern.Split('.');
        for (var i = 0; i < patternSegments.Length; i++)
        {
            if (patternSegments[i] == "*")
            {
                if (i == patternSegments.Length - 1 && patternSegments.Length < 4)
                    return true;   // 結尾的 * 代表剩下的所有段
                continue;
            }
            if (int.Parse(patternSegments[i], CultureInfo.InvariantCulture) != int.Parse(hostSegments[i], CultureInfo.InvariantCulture))
                return false;
        }
        return patternSegments.Length == 4 || patternSegments[^1] == "*";
    }

    [GeneratedRegex("^[a-z0-9-]+(\\.[a-z0-9-]+)*$")]
    private static partial Regex HostNamePattern();
}
