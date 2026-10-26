// 暫時，合併時以 19A 為準（19D 為了能編譯與測試而寫的簡單實作；19A 的正式版會取代整個檔案）。
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumi.Core.Web;

public static class CrawlRules
{
    public const int DefaultMaxPages = 10;
    public const int MaxPages = 20;
    public const int DefaultDepth = 2;
    public const int MaxDepth = 3;
    public const int MaxFilters = 5;
    public static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(1);

    private static readonly string[] LogoutKeywords = ["logout", "signout", "sign-out", "log-out", "logoff", "登出"];

    /// <summary>去掉 # 之後、主機小寫、預設 port 省略；不是 http／https 回 null。</summary>
    public static Uri? Normalize(Uri url)
    {
        if (url.Scheme is not ("http" or "https"))
            return null;
        var builder = new UriBuilder(url)
        {
            Fragment = "",
            Host = url.Host.ToLowerInvariant(),
            Port = url.IsDefaultPort ? -1 : url.Port,
        };
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
        var text = url.AbsoluteUri;
        var inc = include.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (inc.Count > 0 && !inc.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)))
            return false;
        return !exclude.Any(s => !string.IsNullOrWhiteSpace(s) && text.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>網址或連結文字含登出類字樣（D3）。</summary>
    public static bool IsLogoutLink(Uri url, string? linkText) =>
        LogoutKeywords.Any(k =>
            url.AbsoluteUri.Contains(k, StringComparison.OrdinalIgnoreCase)
            || (linkText?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>頁面檔名：<c>{序號三位數}-{標題}.md</c>（D9）。</summary>
    public static string PageFileName(int index, string? title)
    {
        var name = title?.Trim() ?? "";
        if (name.Length > 40)
            name = name[..40];
        if (name.Length == 0)
            name = "page";
        return $"{index:000}-{name}.md";
    }

    /// <summary>輸出子資料夾名稱：<c>{主機}-{yyyyMMdd-HHmmss}</c>（D9）。</summary>
    public static string FolderName(Uri start, DateTimeOffset localNow) =>
        $"{start.Host.ToLowerInvariant().Replace(':', '_')}-{localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
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
        var applies = false;
        var previousWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0)
                line = line[..hash];
            line = line.Trim();
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (line.Length == 0 || colon < 0)
                continue;

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (!previousWasAgent)
                    applies = false;
                applies |= value == "*" || value.Equals("Lumi", StringComparison.OrdinalIgnoreCase);
                previousWasAgent = true;
                continue;
            }

            previousWasAgent = false;
            if (!applies || value.Length == 0)
                continue;
            if (key.Equals("Allow", StringComparison.OrdinalIgnoreCase))
                rules.Add((true, value));
            else if (key.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                rules.Add((false, value));
        }
        return new RobotsRules(rules);
    }

    /// <summary>最長符合者優先；同長度時 Allow 勝；都不符合時允許。比對的是 <c>PathAndQuery</c>，支援 <c>*</c> 與結尾的 <c>$</c>。</summary>
    public bool IsAllowed(Uri url)
    {
        var path = url.PathAndQuery;
        (bool Allow, string Path)? best = null;
        foreach (var rule in Rules)
        {
            if (!Matches(rule.Path, path))
                continue;
            if (best is null || rule.Path.Length > best.Value.Path.Length || (rule.Path.Length == best.Value.Path.Length && rule.Allow))
                best = rule;
        }
        return best?.Allow ?? true;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchorEnd = pattern.EndsWith('$');
        var body = anchorEnd ? pattern[..^1] : pattern;
        var regex = "^" + string.Join(".*", body.Split('*').Select(Regex.Escape)) + (anchorEnd ? "$" : "");
        return Regex.IsMatch(path, regex, RegexOptions.CultureInvariant);
    }
}
