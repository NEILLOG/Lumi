using System.Text;
using Lumi.Core.Browser;
using Lumi.Core.Web;
using Lumi.Tools.Web;
using Xunit;

namespace Lumi.Tools.Tests.Web;

public sealed class WebCrawlerTests : IDisposable
{
    private const string Origin = "https://example.com";
    private const string FolderBase = "example.com-20261026-093000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-crawl-" + Guid.NewGuid().ToString("N"));
    private readonly CrawlSite _site = new();
    private readonly CrawlBrowser _browser;
    private readonly List<TimeSpan> _delays = [];

    public WebCrawlerTests()
    {
        _browser = new CrawlBrowser(_site);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ================= 輔助 =================

    private WebCrawler MakeCrawler(Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(_site, _site, _browser, new FixedTime(), delay ?? ((d, _) =>
        {
            _delays.Add(d);
            return Task.CompletedTask;
        }));

    private static string Url(string path) => Origin + path;

    private void AddPage(string path, string title, params string[] linkPaths) =>
        _site.Pages[Url(path)] = new SitePage(title, $"# {title}\n內容", linkPaths.Select(p => new WebLink(p, new Uri(Url(p)))).ToList());

    private CrawlRequest Req(string start = "https://example.com/", int maxPages = 10, int depth = 2,
        IReadOnlyList<string>? include = null, IReadOnlyList<string>? exclude = null, CrawlMode mode = CrawlMode.Http) =>
        new(new Uri(start), _root, maxPages, depth, include ?? [], exclude ?? [], mode, "c1");

    private static List<string> UrlsOf(CrawlReport report) => report.Pages.Select(p => p.Url.AbsoluteUri).ToList();

    private static string ReadFile(string folder, string name) => File.ReadAllText(Path.Combine(folder, name), Encoding.UTF8);

    // ================= 廣度優先與深度 =================

    [Fact]
    public async Task Crawls_breadth_first_and_stops_at_depth()
    {
        AddPage("/", "首頁", "/a", "/b");
        AddPage("/a", "A", "/a1");
        AddPage("/b", "B", "/b1");
        AddPage("/a1", "A1", "/a1x");
        AddPage("/b1", "B1");
        AddPage("/a1x", "A1X");

        var report = await MakeCrawler().RunAsync(Req(depth: 2), null, default);

        Assert.Equal(
            [Url("/"), Url("/a"), Url("/b"), Url("/a1"), Url("/b1")],
            UrlsOf(report));
        Assert.DoesNotContain(_site.Requests, r => r.Url.AbsolutePath == "/a1x");
        Assert.False(report.Stopped);
    }

    [Fact]
    public async Task Start_page_is_requested_only_once_in_http_mode()
    {
        AddPage("/", "首頁");

        await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal(1, _site.Requests.Count(r => r.Url.AbsolutePath == "/"));
    }

    [Fact]
    public async Task Depth_zero_reads_only_the_start_page()
    {
        AddPage("/", "首頁", "/a");
        AddPage("/a", "A");

        var report = await MakeCrawler().RunAsync(Req(depth: 0), null, default);

        Assert.Equal([Url("/")], UrlsOf(report));
    }

    // ================= 頁數上限 =================

    [Fact]
    public async Task Max_pages_limits_the_number_of_saved_pages()
    {
        AddPage("/", "首頁", "/a", "/b", "/c", "/d");
        AddPage("/a", "A");
        AddPage("/b", "B");
        AddPage("/c", "C");
        AddPage("/d", "D");

        var report = await MakeCrawler().RunAsync(Req(maxPages: 3), null, default);

        Assert.Equal(3, report.Pages.Count);
        Assert.DoesNotContain(_site.Requests, r => r.Url.AbsolutePath == "/d");
    }

    // ================= 同網站、篩選、登出 =================

    [Fact]
    public async Task Only_follows_links_on_the_same_site()
    {
        _site.Pages[Url("/")] = new SitePage("首頁", "首頁", [
            new WebLink("外部", new Uri("https://other.example.net/x")),
            new WebLink("不同 scheme", new Uri("http://example.com/http-only")),
            new WebLink("不同 port", new Uri("https://example.com:8443/port")),
            new WebLink("內部", new Uri(Url("/in"))),
        ]);
        AddPage("/in", "內部");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/in")], UrlsOf(report));
        Assert.DoesNotContain(_site.Requests, r => r.Url.Host != "example.com");
        Assert.DoesNotContain(_site.Requests, r => r.Url.AbsolutePath == "/http-only" || r.Url.AbsolutePath == "/port");
    }

    [Fact]
    public async Task Include_and_exclude_filter_linked_pages_but_not_the_start_page()
    {
        AddPage("/", "首頁", "/docs/a", "/docs/b", "/blog/c", "/docs/archive/d");
        AddPage("/docs/a", "A");
        AddPage("/docs/b", "B");
        AddPage("/blog/c", "C");
        AddPage("/docs/archive/d", "D");

        var report = await MakeCrawler().RunAsync(Req(include: ["/docs/"], exclude: ["archive"]), null, default);

        Assert.Equal([Url("/"), Url("/docs/a"), Url("/docs/b")], UrlsOf(report));
    }

    [Fact]
    public async Task Logout_links_are_skipped_by_url_and_by_link_text()
    {
        _site.Pages[Url("/")] = new SitePage("首頁", "首頁", [
            new WebLink("/a", new Uri(Url("/a"))),
            new WebLink("登出", new Uri(Url("/bye"))),
            new WebLink("sign", new Uri(Url("/sign-out"))),
        ]);
        AddPage("/a", "A");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/a")], UrlsOf(report));
        Assert.DoesNotContain(_site.Requests, r => r.Url.AbsolutePath is "/bye" or "/sign-out");
    }

    // ================= robots.txt（D6）=================

    [Fact]
    public async Task Robots_disallowed_pages_are_not_read_and_are_counted()
    {
        _site.Robots = "User-agent: *\nDisallow: /private\n";
        AddPage("/", "首頁", "/private/x", "/public");
        AddPage("/public", "公開");
        AddPage("/private/x", "私人");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/public")], UrlsOf(report));
        Assert.Equal(1, report.BlockedByRobots);
        Assert.DoesNotContain(_site.Requests, r => r.Url.AbsolutePath == "/private/x");
    }

    [Fact]
    public async Task Robots_blocking_the_start_page_throws_with_a_message()
    {
        _site.Robots = "User-agent: Lumi\nDisallow: /\n";
        AddPage("/", "首頁");

        var ex = await Assert.ThrowsAsync<CrawlException>(() => MakeCrawler().RunAsync(Req(), null, default));

        Assert.Equal("這個網站的 robots.txt 不允許爬取這個網址。", ex.Message);
        Assert.DoesNotContain(_site.Requests, r => r.Url.AbsolutePath == "/");
    }

    [Fact]
    public async Task Unreadable_robots_txt_allows_everything()
    {
        _site.ConnectionErrors[Url("/robots.txt")] = new WebRequestException(WebFailureKind.Connection, "無法連線。");
        AddPage("/", "首頁", "/a");
        AddPage("/a", "A");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/a")], UrlsOf(report));
        Assert.Equal(0, report.BlockedByRobots);
    }

    [Fact]
    public async Task Missing_robots_txt_allows_everything()
    {
        AddPage("/", "首頁", "/a");
        AddPage("/a", "A");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/a")], UrlsOf(report));
    }

    // ================= 讀法（D4）=================

    [Fact]
    public async Task Auto_switches_to_browser_when_start_page_is_unauthorized()
    {
        _site.Pages[Url("/")] = new SitePage("內網", "登入後的內容", []) { Status = 401 };

        var report = await MakeCrawler().RunAsync(Req(mode: CrawlMode.Auto), null, default);

        Assert.True(report.UsedBrowser);
        Assert.Contains($"c1#crawl {Url("/")}", _browser.Opened);
        Assert.Equal([Url("/")], UrlsOf(report));
    }

    [Fact]
    public async Task Auto_switches_to_browser_when_start_page_looks_like_a_script_app()
    {
        _site.Pages[Url("/")] = new SitePage("應用程式", "載入中", []) { Html = "<html><body><script>app()</script></body></html>" };

        var report = await MakeCrawler().RunAsync(Req(mode: CrawlMode.Auto), null, default);

        Assert.True(report.UsedBrowser);
        Assert.Contains($"c1#crawl {Url("/")}", _browser.Opened);
    }

    [Fact]
    public async Task Auto_keeps_http_when_script_page_has_enough_text()
    {
        _site.Pages[Url("/")] = new SitePage("長文", new string('字', 300), []) { Html = "<html><script>x()</script></html>" };

        var report = await MakeCrawler().RunAsync(Req(mode: CrawlMode.Auto), null, default);

        Assert.False(report.UsedBrowser);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task Auto_keeps_http_with_a_note_when_browser_is_unavailable()
    {
        _browser.Availability = new BrowserAvailability(null, [], "找不到 Microsoft Edge 或 Google Chrome。");
        _site.Pages[Url("/")] = new SitePage("應用程式", "載入中", [new WebLink("a", new Uri(Url("/a")))]) { Html = "<script>app()</script>" };
        AddPage("/a", "A");

        var report = await MakeCrawler().RunAsync(Req(mode: CrawlMode.Auto), null, default);

        Assert.False(report.UsedBrowser);
        Assert.Contains(report.Notes, n => n.Contains("改用 HTTP 讀取"));
        Assert.Equal([Url("/"), Url("/a")], UrlsOf(report));
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task Auto_with_unauthorized_start_and_no_browser_throws_the_status()
    {
        _browser.Availability = new BrowserAvailability(null, [], "找不到瀏覽器。");
        _site.Pages[Url("/")] = new SitePage("內網", "", []) { Status = 401 };

        var ex = await Assert.ThrowsAsync<CrawlException>(() => MakeCrawler().RunAsync(Req(mode: CrawlMode.Auto), null, default));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Browser_mode_without_a_browser_throws_the_availability_problem()
    {
        const string problem = "找不到 Microsoft Edge 或 Google Chrome，請先安裝 Microsoft Edge。";
        _browser.Availability = new BrowserAvailability(null, [], problem);
        AddPage("/", "首頁");

        var ex = await Assert.ThrowsAsync<CrawlException>(() => MakeCrawler().RunAsync(Req(mode: CrawlMode.Browser), null, default));

        Assert.Equal(problem, ex.Message);
    }

    [Fact]
    public async Task Browser_mode_uses_the_crawl_session_and_closes_it()
    {
        AddPage("/", "首頁", "/a");
        AddPage("/a", "A");

        var report = await MakeCrawler().RunAsync(Req(mode: CrawlMode.Browser), null, default);

        Assert.True(report.UsedBrowser);
        Assert.Equal([Url("/"), Url("/a")], UrlsOf(report));
        Assert.All(_browser.Opened, call => Assert.StartsWith("c1#crawl ", call));
        Assert.Contains("c1#crawl", _browser.Closed);
        Assert.Contains("- 讀法：瀏覽器（使用 Lumi 瀏覽器的登入狀態）", ReadFile(report.Folder, "index.md"));
    }

    [Fact]
    public async Task Browser_failures_are_recorded_and_the_session_is_still_closed()
    {
        _browser.FailingUrls.Add(Url("/a"));
        AddPage("/", "首頁", "/a", "/b");
        AddPage("/a", "A");
        AddPage("/b", "B");

        var report = await MakeCrawler().RunAsync(Req(mode: CrawlMode.Browser), null, default);

        Assert.Equal([Url("/"), Url("/b")], UrlsOf(report));
        Assert.Contains(report.Failures, f => f.Url.AbsoluteUri == Url("/a") && f.Reason == "無法開啟這個網址。");
        Assert.Contains("c1#crawl", _browser.Closed);
    }

    // ================= 非 HTML、失敗、轉址（D8）=================

    [Fact]
    public async Task Non_html_responses_are_listed_as_file_links_not_saved()
    {
        AddPage("/", "首頁", "/file.pdf");
        _site.Pages[Url("/file.pdf")] = new SitePage("", "", []) { MediaType = "application/pdf" };

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/")], UrlsOf(report));
        Assert.Equal([Url("/file.pdf")], report.FileLinks.Select(u => u.AbsoluteUri).ToList());
    }

    [Fact]
    public async Task A_failing_page_is_recorded_and_the_crawl_continues()
    {
        AddPage("/", "首頁", "/a", "/c", "/b");
        _site.Pages[Url("/a")] = new SitePage("A", "", []) { Status = 500 };
        _site.ConnectionErrors[Url("/c")] = new WebRequestException(WebFailureKind.Connection, "無法連線到這個網頁。");
        AddPage("/b", "B");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/b")], UrlsOf(report));
        Assert.Contains(report.Failures, f => f.Url.AbsoluteUri == Url("/a") && f.Reason == "HTTP 500");
        Assert.Contains(report.Failures, f => f.Url.AbsoluteUri == Url("/c") && f.Reason == "無法連線到這個網頁。");
    }

    [Fact]
    public async Task Redirect_to_another_site_is_a_failure()
    {
        _site.Pages[Url("/a")] = new SitePage("A", "", []) { RedirectTo = "https://other.example.net/x" };
        _site.Pages["https://other.example.net/x"] = new SitePage("外部", "外部內容", []);
        AddPage("/", "首頁", "/a");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/")], UrlsOf(report));
        Assert.Contains(report.Failures, f => f.Url.AbsoluteUri == Url("/a") && f.Reason == "轉址到其他網站");
    }

    [Fact]
    public async Task Redirect_within_the_same_site_is_saved_under_the_final_url()
    {
        _site.Pages[Url("/old")] = new SitePage("舊", "", []) { RedirectTo = Url("/new") };
        AddPage("/new", "新");
        AddPage("/", "首頁", "/old");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([Url("/"), Url("/new")], UrlsOf(report));
    }

    [Fact]
    public async Task Start_page_connection_error_throws_with_the_message()
    {
        _site.ConnectionErrors[Url("/")] = new WebRequestException(WebFailureKind.NameResolution, "找不到這個網站。");

        var ex = await Assert.ThrowsAsync<CrawlException>(() => MakeCrawler().RunAsync(Req(), null, default));

        Assert.Equal("找不到這個網站。", ex.Message);
    }

    [Fact]
    public async Task Start_page_http_error_throws_with_the_status()
    {
        _site.Pages[Url("/")] = new SitePage("首頁", "", []) { Status = 500 };

        var ex = await Assert.ThrowsAsync<CrawlException>(() => MakeCrawler().RunAsync(Req(), null, default));

        Assert.Equal("起始頁回應 500 Error。", ex.Message);
    }

    // ================= 寫檔（D9）=================

    [Fact]
    public async Task Writes_a_new_folder_with_page_files_and_an_index()
    {
        _site.Pages[Url("/")] = new SitePage("首頁", "本文內容", [new WebLink("關於", new Uri(Url("/a")))]);
        AddPage("/a", "關於");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal(Path.Combine(_root, FolderBase), report.Folder);
        Assert.Equal("001-首頁.md", report.Pages[0].FileName);
        Assert.Equal("002-關於.md", report.Pages[1].FileName);
        Assert.Equal(
            "# 首頁\n\n來源：https://example.com/\n擷取時間：2026-10-26 09:30\n\n---\n\n本文內容",
            ReadFile(report.Folder, "001-首頁.md"));
        var bytes = File.ReadAllBytes(Path.Combine(report.Folder, "002-關於.md"));
        Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));

        var index = ReadFile(report.Folder, "index.md");
        Assert.Contains("# 爬取結果：example.com", index);
        Assert.Contains("1. [首頁](001-首頁.md) — https://example.com/", index);
        Assert.Contains("2. [關於](002-關於.md) — https://example.com/a", index);
        Assert.Contains("- 讀法：HTTP", index);
    }

    [Fact]
    public async Task Index_lists_robots_failures_file_links_and_the_browser_mode()
    {
        _site.Robots = "User-agent: *\nDisallow: /private\n";
        _site.Pages[Url("/")] = new SitePage("首頁", "首頁", [
            new WebLink("私人", new Uri(Url("/private/x"))),
            new WebLink("A", new Uri(Url("/a"))),
            new WebLink("檔案", new Uri(Url("/file.pdf"))),
        ]);
        AddPage("/a", "A");
        _site.ConnectionErrors[Url("/private/x")] = new WebRequestException(WebFailureKind.Other, "不應該被讀到。");
        _site.Pages[Url("/file.pdf")] = new SitePage("", "", []) { MediaType = "application/pdf" };
        _site.ConnectionErrors[Url("/a")] = new WebRequestException(WebFailureKind.Connection, "無法連線。");

        var report = await MakeCrawler().RunAsync(Req(), null, default);
        var index = ReadFile(report.Folder, "index.md");

        Assert.Contains("- 讀法：HTTP", index);
        Assert.Contains("被 robots.txt 擋下：1 個網址", index);
        Assert.Contains("## 讀取失敗", index);
        Assert.Contains($"- {Url("/a")}：無法連線。", index);
        Assert.Contains("## 其他檔案連結", index);
        Assert.Contains($"- {Url("/file.pdf")}", index);
    }

    [Fact]
    public async Task Existing_folder_gets_a_numeric_suffix()
    {
        Directory.CreateDirectory(Path.Combine(_root, FolderBase));
        AddPage("/", "首頁");

        var report = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal(Path.Combine(_root, FolderBase + "-2"), report.Folder);
        Assert.True(File.Exists(Path.Combine(report.Folder, "index.md")));
    }

    [Fact]
    public async Task Never_overwrites_files_in_an_existing_folder()
    {
        AddPage("/", "首頁");
        var first = await MakeCrawler().RunAsync(Req(), null, default);
        var original = ReadFile(first.Folder, "001-首頁.md");

        var second = await MakeCrawler().RunAsync(Req(), null, default);

        Assert.NotEqual(first.Folder, second.Folder);
        Assert.Equal(original, ReadFile(first.Folder, "001-首頁.md"));
    }

    // ================= 進度與限速（D7、D12）=================

    [Fact]
    public async Task Reports_progress_before_each_page_read()
    {
        AddPage("/", "首頁", "/a", "/b", "/c");
        AddPage("/a", "A");
        AddPage("/b", "B");
        AddPage("/c", "C");
        var progress = new ListProgress();

        await MakeCrawler().RunAsync(Req(maxPages: 3), progress, default);

        Assert.Equal(["第 1／3 頁", "第 2／3 頁", "第 3／3 頁"], progress.Messages);
    }

    [Fact]
    public async Task Waits_one_page_delay_between_reads_but_not_before_the_first()
    {
        AddPage("/", "首頁", "/a", "/b");
        AddPage("/a", "A");
        AddPage("/b", "B");

        await MakeCrawler().RunAsync(Req(), null, default);

        Assert.Equal([CrawlRules.PageDelay, CrawlRules.PageDelay], _delays);
    }

    // ================= 取消（D1）=================

    [Fact]
    public async Task Cancelling_keeps_saved_pages_and_writes_the_index()
    {
        AddPage("/", "首頁", "/a", "/b");
        AddPage("/a", "A");
        AddPage("/b", "B");
        using var cts = new CancellationTokenSource();
        var crawler = MakeCrawler((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        var report = await crawler.RunAsync(Req(), null, cts.Token);

        Assert.True(report.Stopped);
        Assert.Single(report.Pages);
        Assert.Contains("使用者中途停止，只存了前 1 頁。", ReadFile(report.Folder, "index.md"));
        Assert.True(File.Exists(Path.Combine(report.Folder, "001-首頁.md")));
    }

    // ================= 測試用的假件 =================

    private sealed record SitePage(string Title, string Markdown, IReadOnlyList<WebLink> Links)
    {
        public int Status { get; init; } = 200;

        public string MediaType { get; init; } = "text/html";

        public string Html { get; init; } = "<html><body>內容</body></html>";

        public string? RedirectTo { get; init; }
    }

    /// <summary>同時扮演 <see cref="IWebClient"/> 與 <see cref="IWebContentExtractor"/>：依網址回傳預先設定的頁面。</summary>
    private sealed class CrawlSite : IWebClient, IWebContentExtractor
    {
        public Dictionary<string, SitePage> Pages { get; } = [];

        public Dictionary<string, WebRequestException> ConnectionErrors { get; } = [];

        /// <summary>robots.txt 的內容；null＝404。</summary>
        public string? Robots { get; set; }

        public List<WebRequest> Requests { get; } = [];

        public Task<WebResponse> SendAsync(WebRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            var key = request.Url.AbsoluteUri;
            if (ConnectionErrors.TryGetValue(key, out var error))
                throw error;

            if (request.Url.AbsolutePath == "/robots.txt")
            {
                return Task.FromResult(Robots is null
                    ? Response(404, "Not Found", request.Url, "text/html", "missing")
                    : Response(200, "OK", request.Url, "text/plain", Robots));
            }

            if (!Pages.TryGetValue(key, out var page))
                return Task.FromResult(Response(404, "Not Found", request.Url, "text/html", "missing"));

            var finalUrl = page.RedirectTo is null ? request.Url : new Uri(page.RedirectTo);
            return Task.FromResult(Response(page.Status, page.Status == 401 ? "Unauthorized" : page.Status < 400 ? "OK" : "Error", finalUrl, page.MediaType, page.Html));
        }

        public Task<WebDownloadResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct) => throw new NotSupportedException();

        private static WebResponse Response(int status, string reason, Uri finalUrl, string mediaType, string body) =>
            new(status, reason, finalUrl, mediaType, "utf-8", [], Encoding.UTF8.GetBytes(body), false);

        public WebPage Extract(string html, Uri baseUrl, bool readable)
        {
            if (Pages.TryGetValue(baseUrl.AbsoluteUri, out var page))
                return new WebPage(page.Title, page.Markdown, page.Links, readable);
            return new WebPage(null, "", [], readable);
        }

        public string DecodeText(byte[] body, string? charset, bool isHtml) => Encoding.UTF8.GetString(body);
    }

    /// <summary>瀏覽器假件：<see cref="OpenAsync"/> 把目前網址設為指定頁面；只實作爬取會用到的方法。</summary>
    private sealed class CrawlBrowser(CrawlSite site) : IBrowserSessions
    {
        private Uri? _current;

        public BrowserAvailability Availability { get; set; } = new(new InstalledBrowser(BrowserKind.Edge, "msedge.exe", "1"), [], null);

        public List<string> Opened { get; } = [];

        public List<string> Closed { get; } = [];

        public HashSet<string> FailingUrls { get; } = [];

        public BrowserAvailability CheckAvailability() => Availability;

        public Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct)
        {
            Opened.Add($"{conversationId} {url.AbsoluteUri}");
            if (FailingUrls.Contains(url.AbsoluteUri))
                throw new BrowserException(BrowserFailureKind.Navigation, "無法開啟這個網址。");
            _current = url;
            return Task.FromResult(new BrowserOutcome("已開啟。", new BrowserSnapshot(url, null, "", 0, false)));
        }

        public Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct)
        {
            if (_current is null || !site.Pages.TryGetValue(_current.AbsoluteUri, out var page))
                throw new BrowserException(BrowserFailureKind.Navigation, "頁面不存在。");
            return Task.FromResult(new BrowserPageContent(_current, page.Title, page.Html, page.Markdown));
        }

        public Task CloseAsync(string conversationId, CancellationToken ct)
        {
            Closed.Add(conversationId);
            return Task.CompletedTask;
        }

        public Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct) => throw new NotSupportedException();

        public Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();

        public Uri? CurrentUrl(string conversationId) => _current;

        public string? DescribeRef(string conversationId, string @ref) => null;

        public Task CloseAllAsync() => throw new NotSupportedException();

        public Task ClearProfileAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 26, 9, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class ListProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
