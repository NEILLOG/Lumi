using Lumi.Browser.Tests.TestSupport;
using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Tools;
using Lumi.Core.Web;
using Lumi.Tools.Web;
using Lumi.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lumi.Browser.Tests;

/// <summary>
/// 端到端：真正的 <c>web_crawl</c> 工具，搭配真的 Lumi.Web（HTTP、HTML 轉 Markdown）與真的瀏覽器工作階段，
/// 爬一個本機測試站（首頁連到 A、B、登出、被 robots.txt 擋的私有頁、PDF、外部網站；A 再連到 C）。
/// </summary>
public sealed class WebCrawlEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-crawl-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services;
    private readonly BrowserTestRig _rig = new();

    public WebCrawlEndToEndTests()
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(FakeCatalog.Create(AppSettings.Empty));
        services.AddSingleton<ISecretStore, NoSecrets>();
        services.AddLumiWeb();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _rig.Dispose();
        _services.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存資料夾刪不掉不影響測試結果
        }
    }

    private WebCrawlTool NewTool() => new(
        _services.GetRequiredService<IWebClient>(), _services.GetRequiredService<IWebContentExtractor>(), _rig.Sessions);

    private ToolContext NewContext(List<string>? progress = null) => new(
        "crawl-" + Guid.NewGuid().ToString("N"), ChatMode.Workbench, TimeProvider.System, new OneFolderScope(_root),
        progress is null ? null : new CollectingProgress(progress));

    private async Task<(ToolResult Result, string Folder)> CrawlAsync(string json, List<string>? progress = null)
    {
        var result = await NewTool().ExecuteAsync(json, NewContext(progress), CancellationToken.None);
        Assert.False(result.IsError, result.Content);
        var folder = Assert.Single(Directory.GetDirectories(Path.Combine(_root, "out")));
        return (result, folder);
    }

    private static string Args(Uri start, string mode, string extra = "") =>
        $$"""{"url":"{{start.AbsoluteUri}}","path":"out","mode":"{{mode}}","depth":2,"max_pages":10{{extra}}}""";

    private void AssertTheTestSiteWasCrawledCorrectly(string folder, string result)
    {
        var files = Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToList();
        Assert.Contains("index.md", files);
        var pages = files.Where(f => f != "index.md").ToList();
        Assert.Equal(4, pages.Count);   // 首頁、A、B、C
        Assert.Contains(pages, f => f.Contains("頁面 A", StringComparison.Ordinal));
        Assert.Contains(pages, f => f.Contains("頁面 C", StringComparison.Ordinal));

        // 登出連結與被 robots.txt 擋的頁面根本沒有被請求；外部網站與 PDF 也沒有
        Assert.DoesNotContain(_rig.Site.Requests, r => r.StartsWith("/crawl/logout", StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Site.Requests, r => r.StartsWith("/crawl/private", StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Site.Requests, r => r.EndsWith(".pdf", StringComparison.Ordinal) && !r.StartsWith("/crawl/file.pdf", StringComparison.Ordinal));

        var index = File.ReadAllText(Path.Combine(folder, "index.md"));
        Assert.Contains("頁面 A", index);
        Assert.Contains("file.pdf", index);                 // 其他檔案連結
        Assert.Contains("robots.txt", index);               // 被擋下的數量

        Assert.Contains("<web_content", result);
        Assert.Contains("index.md", result);
    }

    [Fact]
    public async Task Http_mode_crawls_the_local_site_and_saves_markdown_files()
    {
        var progress = new List<string>();

        var (result, folder) = await CrawlAsync(Args(_rig.Url("/crawl/"), "http"), progress);

        AssertTheTestSiteWasCrawledCorrectly(folder, result.Content);
        Assert.Contains("HTTP", result.Content);
        Assert.Contains(progress, p => p.Contains("第 1／10 頁", StringComparison.Ordinal));
        Assert.True(progress.Count >= 4);
        var firstPage = File.ReadAllText(Directory.GetFiles(folder, "001-*.md").Single());
        Assert.StartsWith("# 爬取首頁", firstPage, StringComparison.Ordinal);
        Assert.Contains("來源：", firstPage);
    }

    [RealBrowserFact]
    public async Task Browser_mode_crawls_the_same_site_through_the_real_browser()
    {
        var (result, folder) = await CrawlAsync(Args(_rig.Url("/crawl/"), "browser"));

        AssertTheTestSiteWasCrawledCorrectly(folder, result.Content);
        Assert.Contains("瀏覽器", result.Content);
    }

    [Fact]
    public async Task Max_pages_stops_the_crawl_early()
    {
        var (result, folder) = await CrawlAsync(Args(_rig.Url("/crawl/"), "http").Replace("\"max_pages\":10", "\"max_pages\":2"));

        Assert.Equal(2, Directory.GetFiles(folder).Count(f => !f.EndsWith("index.md", StringComparison.Ordinal)));
        Assert.Contains("2 頁", result.Summary);
    }

    private sealed class NoSecrets : ISecretStore
    {
        public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<SecretValue?>(null);

        public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class CollectingProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }

    /// <summary>範圍只有一個資料夾的簡單實作。</summary>
    private sealed class OneFolderScope(string root) : IFileScope
    {
        private readonly ScopeRoot _root = new("root", root, true);

        public IReadOnlyList<ScopeRoot> Roots => [_root];

        public ScopeResolution Resolve(string path)
        {
            var full = Path.GetFullPath(Path.Combine(root, path));
            return full.StartsWith(root, StringComparison.Ordinal) ? new ScopeResolution(full, _root, null) : ScopeResolution.Denied("範圍外。");
        }

        public string ToDisplayPath(string fullPath) => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }
}
