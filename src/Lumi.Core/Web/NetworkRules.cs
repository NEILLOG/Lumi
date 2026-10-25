using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumi.Core.Web;

/// <summary>
/// 網路設定的欄位驗證（M17）：純函式，回傳給使用者看的原因，合法時是 null。
/// 【暫時版】17G 為了編譯與測試而寫；17A 合併時以 17A 的 NetworkRules 為準。
/// </summary>
public static partial class NetworkRules
{
    [GeneratedRegex(@"^(\*\.)?[a-z0-9.-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[\d.*]+$")]
    private static partial Regex Ipv4Like();

    [GeneratedRegex(@":(\d+)$")]
    private static partial Regex PortSuffix();

    /// <summary>依換行切開、去頭尾空白、去空行。</summary>
    public static IReadOnlyList<string> ParseLines(string? multiline) =>
        (multiline ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    public static string? ValidatePacUrl(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0)
            return "請輸入 PAC 網址";
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file"
            ? null
            : "請輸入 http、https 或 file 開頭的完整網址";
    }

    public static string? ValidateManualProxy(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0)
            return "請輸入 Proxy 位址";
        if (t.Contains('@'))
            return "位址不可包含帳號密碼，請改用下方的驗證設定";
        return TryParseManualProxy(t, out _) ? null : "請輸入主機與連接埠，例如 proxy.example.com:8080";
    }

    /// <summary>沒有 <c>://</c> 時補上 <c>http://</c>；scheme 只能是 http、socks5、socks4；一定要寫出 port；路徑只能是空或 /。</summary>
    public static bool TryParseManualProxy(string text, out Uri proxy)
    {
        proxy = null!;
        var t = text.Trim();
        if (t.Length == 0)
            return false;
        if (!t.Contains("://", StringComparison.Ordinal))
            t = "http://" + t;

        var authority = t[(t.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var portMatch = PortSuffix().Match(authority);
        if (!portMatch.Success
            || !int.TryParse(portMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
            return false;

        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "socks5" or "socks4")
            || uri.UserInfo.Length > 0
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
            return false;

        proxy = uri;
        return true;
    }

    public static string? ValidateHostPattern(string line)
    {
        var l = line.Trim();
        if (Ipv4Like().IsMatch(l))
        {
            var segments = l.Split('.');
            var valid = segments.Length is >= 1 and <= 4
                && segments.All(s => s == "*" || (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 255))
                && segments.Any(s => s != "*");
            return valid ? null : $"「{line}」不是有效的主機名稱";
        }
        return HostPattern().IsMatch(l) && !l.Contains("*.*", StringComparison.Ordinal) ? null : $"「{line}」不是有效的主機名稱";
    }

    public static string? ValidateHostPatterns(IEnumerable<string> lines) =>
        lines.Select(ValidateHostPattern).FirstOrDefault(e => e is not null);

    /// <summary>
    /// 不分大小寫。<c>*.example.com</c> 符合 example.com 與它的所有子網域；IPv4 樣式（例如 <c>10.*</c>）逐段比對，
    /// <c>*</c> 符合任何一段，主機必須是 IPv4 且段數不少於樣式；其他是完全相同。
    /// </summary>
    public static bool HostMatches(string host, IReadOnlyList<string> patterns)
    {
        var h = host.Trim().ToLowerInvariant();
        foreach (var raw in patterns)
        {
            var p = raw.Trim().ToLowerInvariant();
            if (p.StartsWith("*.", StringComparison.Ordinal))
            {
                if (h == p[2..] || h.EndsWith(p[1..], StringComparison.Ordinal))
                    return true;
            }
            else if (Ipv4Like().IsMatch(p))
            {
                var hs = h.Split('.');
                var ps = p.Split('.');
                if (hs.Length >= ps.Length && hs.All(s => Ipv4Like().IsMatch(s))
                    && ps.Select((seg, i) => seg == "*" || seg == hs[i]).All(ok => ok))
                    return true;
            }
            else if (h == p)
            {
                return true;
            }
        }
        return false;
    }

    public static string? ValidateProxyUser(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "請輸入帳號" : null;

    private static readonly string TimeoutRangeMessage =
        $"請輸入 {NetworkSettings.MinTimeoutSeconds} 到 {NetworkSettings.MaxTimeoutSeconds} 之間的整數";

    /// <summary>整數 5～120（含）。</summary>
    public static string? ValidateTimeout(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
        && seconds is >= NetworkSettings.MinTimeoutSeconds and <= NetworkSettings.MaxTimeoutSeconds
            ? null
            : TimeoutRangeMessage;

    public static string? ValidateTestUrl(string? text)
    {
        var t = text?.Trim() ?? "";
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? null
            : "請輸入 http 或 https 開頭的完整網址";
    }

    /// <summary>依模式檢查需要的欄位，回第一個錯誤；全部合法時是 null。</summary>
    public static string? Validate(NetworkSettings s)
    {
        string? error = null;
        if (s.Mode == ProxyMode.Pac)
            error = ValidatePacUrl(s.PacUrl);
        if (error is null && s.Mode == ProxyMode.Manual)
            error = ValidateManualProxy(s.ManualProxy) ?? ValidateHostPatterns(s.BypassList);
        if (error is null && s.Auth == ProxyAuthMode.Account && s.Mode != ProxyMode.None)
            error = ValidateProxyUser(s.ProxyUser);
        error ??= ValidateHostPatterns(s.IntegratedAuthSites);
        if (error is null && (s.TimeoutSeconds < NetworkSettings.MinTimeoutSeconds || s.TimeoutSeconds > NetworkSettings.MaxTimeoutSeconds))
            error = TimeoutRangeMessage;
        return error;
    }
}
