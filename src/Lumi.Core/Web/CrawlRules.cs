namespace Lumi.Core.Web;

public static class CrawlRules
{
    public const int DefaultMaxPages = 10;
    public const int MaxPages = 20;
    public const int DefaultDepth = 2;
    public const int MaxDepth = 3;
    public const int MaxFilters = 5;
    public static readonly TimeSpan PageDelay = TimeSpan.FromSeconds(1);

    /// <summary>去掉 # 之後、主機小寫、預設 port 省略；不是 http／https 回 null。</summary>
    public static Uri? Normalize(Uri url) => throw new NotImplementedException();

    /// <summary>scheme、主機、port 都相同。</summary>
    public static bool SameSite(Uri a, Uri b) => throw new NotImplementedException();

    /// <summary>include 有值時網址要包含其中之一；exclude 有任何一個符合就排除（都不分大小寫）。</summary>
    public static bool PassesFilters(Uri url, IReadOnlyList<string> include, IReadOnlyList<string> exclude) => throw new NotImplementedException();

    /// <summary>網址或連結文字含登出類字樣（D3）。</summary>
    public static bool IsLogoutLink(Uri url, string? linkText) => throw new NotImplementedException();

    /// <summary>頁面檔名：<c>{序號三位數}-{標題}.md</c>（D9）。</summary>
    public static string PageFileName(int index, string? title) => throw new NotImplementedException();

    /// <summary>輸出子資料夾名稱：<c>{主機}-{yyyyMMdd-HHmmss}</c>（D9）。</summary>
    public static string FolderName(Uri start, DateTimeOffset localNow) => throw new NotImplementedException();
}

/// <summary>robots.txt 的規則（D6）。</summary>
public sealed class RobotsRules
{
    public static readonly RobotsRules AllowAll = new([]);

    public RobotsRules(IReadOnlyList<(bool Allow, string Path)> rules) => Rules = rules;

    public IReadOnlyList<(bool Allow, string Path)> Rules { get; }

    /// <summary>只取 <c>User-agent: *</c> 與 <c>User-agent: Lumi</c>（不分大小寫）群組的 Allow／Disallow；壞掉的行略過。</summary>
    public static RobotsRules Parse(string text) => throw new NotImplementedException();

    /// <summary>最長符合者優先；同長度時 Allow 勝；都不符合時允許。比對的是 <c>PathAndQuery</c>，支援 <c>*</c> 與結尾的 <c>$</c>。</summary>
    public bool IsAllowed(Uri url) => throw new NotImplementedException();
}
