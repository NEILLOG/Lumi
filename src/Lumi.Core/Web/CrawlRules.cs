using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Core.Web;

/// <summary>網站爬取的網址規則與檔名規則（M19，純函式）。</summary>
public static class CrawlRules
{
    public const int DefaultMaxPages = 10;
    public const int MaxPages = 20;
    public const int DefaultDepth = 2;
    public const int MaxDepth = 3;
    public const int MaxFilters = 5;
    public static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(1);

    private const int MaxTitleLength = 40;

    private static readonly string[] LogoutKeywords =
        ["logout", "signout", "sign-out", "log-out", "logoff", "登出"];

    /// <summary>去掉 # 之後、主機小寫、預設 port 省略；不是 http／https 回 null。</summary>
    public static Uri? Normalize(Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
            return null;

        var builder = new UriBuilder(url)
        {
            Fragment = "",
            Host = url.Host.ToLowerInvariant(),
        };
        if (string.IsNullOrEmpty(builder.Path))
            builder.Path = "/";
        if (builder.Port == DefaultPort(url.Scheme))
            builder.Port = -1;
        return builder.Uri;
    }

    /// <summary>scheme、主機、port 都相同。</summary>
    public static bool SameSite(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>include 有值時網址要包含其中之一；exclude 有任何一個符合就排除（都不分大小寫）。</summary>
    public static bool PassesFilters(Uri url, IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        var includes = include.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (includes.Count > 0 && !includes.Any(s => ContainsInUrl(url, s)))
            return false;

        return !exclude.Any(s => !string.IsNullOrWhiteSpace(s) && ContainsInUrl(url, s));
    }

    /// <summary>
    /// 路徑的副檔名是常見的檔案類型（PDF、Office、壓縮檔、圖片、影音、安裝檔…）：不當成網頁讀，只列在目錄的「其他檔案連結」。
    /// 看不出來的（沒有副檔名、<c>.html</c>、<c>.php</c> 等）一律當網頁，HTTP 模式再用回應的媒體類型確認。
    /// </summary>
    public static bool LooksLikeFile(Uri url)
    {
        var path = url.AbsolutePath;
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot < path.LastIndexOf('/'))
            return false;
        return FileExtensions.Contains(path[dot..]);
    }

    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".csv", ".zip", ".rar", ".7z", ".gz", ".tar",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".bmp", ".mp3", ".mp4", ".wav", ".mov", ".avi", ".webm",
        ".exe", ".msi", ".dmg", ".pkg", ".iso", ".apk",
    };

    /// <summary>網址或連結文字含登出類字樣（D3）。</summary>
    public static bool IsLogoutLink(Uri url, string? linkText)
    {
        foreach (var keyword in LogoutKeywords)
        {
            if (ContainsInUrl(url, keyword))
                return true;
            if (linkText is not null && linkText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>頁面檔名：<c>{序號三位數}-{標題}.md</c>（D9）。</summary>
    public static string PageFileName(int index, string? title)
    {
        var name = (title ?? "").Trim();

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var replaced = new StringBuilder(name.Length);
        foreach (var c in name)
            replaced.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);

        var collapsed = Regex.Replace(replaced.ToString(), @"\s+", " ");
        var shortened = string.Concat(collapsed.EnumerateRunes().Take(MaxTitleLength).Select(r => r.ToString()));
        var trimmed = TrimTrailingDotsAndSpaces(shortened);

        if (trimmed.Length == 0)
            trimmed = "page";
        return $"{index:000}-{trimmed}.md";
    }

    /// <summary>輸出子資料夾名稱：<c>{主機}-{yyyyMMdd-HHmmss}</c>（D9）。</summary>
    public static string FolderName(Uri start, DateTimeOffset localNow)
    {
        var host = start.Host.ToLowerInvariant().Replace(':', '_');
        return $"{host}-{localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 網址是否含有文字（不分大小寫）。<see cref="Uri.AbsoluteUri"/> 會把中文百分比編碼，
    /// 所以同時比對編碼前後的文字，讓「登出」「/文件/」這類中文字樣也能比對到。
    /// </summary>
    private static bool ContainsInUrl(Uri url, string text) =>
        url.AbsoluteUri.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Uri.UnescapeDataString(url.AbsoluteUri).Contains(text, StringComparison.OrdinalIgnoreCase);

    private static int DefaultPort(string scheme) => scheme == "https" ? 443 : 80;

    private static string TrimTrailingDotsAndSpaces(string value)
    {
        var end = value.Length;
        while (end > 0 && (value[end - 1] == '.' || char.IsWhiteSpace(value[end - 1])))
            end--;
        return value[..end];
    }
}

/// <summary>robots.txt 的規則（D6）。</summary>
public sealed class RobotsRules
{
    public static readonly RobotsRules AllowAll = new([]);

    public RobotsRules(IReadOnlyList<(bool Allow, string Path)> rules) => Rules = rules;

    public IReadOnlyList<(bool Allow, string Path)> Rules { get; }

    /// <summary>只取 <c>User-agent: *</c> 與 <c>User-agent: Lumi</c>（不分大小寫）群組的 Allow／Disallow；壞掉的行略過。</summary>
    public static RobotsRules Parse(string text)
    {
        var rules = new List<(bool Allow, string Path)>();
        var appliesToUs = false;
        var lastWasUserAgent = false;

        foreach (var rawLine in (text ?? "").Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var hash = rawLine.IndexOf('#');
            var line = (hash < 0 ? rawLine : rawLine[..hash]).Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
                continue;   // 空行、沒有冒號的壞掉的行

            var field = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                // 連續的 User-agent 行屬於同一群組；否則開始新群組
                var matches = value.Equals("*", StringComparison.Ordinal)
                    || value.Equals("Lumi", StringComparison.OrdinalIgnoreCase);
                appliesToUs = lastWasUserAgent ? appliesToUs || matches : matches;
                lastWasUserAgent = true;
                continue;
            }

            lastWasUserAgent = false;
            if (!appliesToUs || value.Length == 0)
                continue;

            if (field.Equals("Allow", StringComparison.OrdinalIgnoreCase))
                rules.Add((true, value));
            else if (field.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                rules.Add((false, value));
        }

        return new RobotsRules(rules);
    }

    /// <summary>最長符合者優先；同長度時 Allow 勝；都不符合時允許。比對的是 <c>PathAndQuery</c>，支援 <c>*</c> 與結尾的 <c>$</c>。</summary>
    public bool IsAllowed(Uri url)
    {
        var target = url.PathAndQuery;
        (bool Allow, string Path)? best = null;

        foreach (var rule in Rules)
        {
            if (!PatternMatches(rule.Path, target))
                continue;
            if (best is null
                || rule.Path.Length > best.Value.Path.Length
                || (rule.Path.Length == best.Value.Path.Length && rule.Allow))
            {
                best = rule;
            }
        }

        return best?.Allow ?? true;
    }

    private static bool PatternMatches(string pattern, string target)
    {
        var anchoredEnd = pattern.EndsWith('$');
        var body = anchoredEnd ? pattern[..^1] : pattern;

        var regex = new StringBuilder("^");
        var parts = body.Split('*');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                regex.Append(".*");
            regex.Append(Regex.Escape(parts[i]));
        }
        if (anchoredEnd)
            regex.Append(@"\z");

        return Regex.IsMatch(target, regex.ToString());
    }
}
