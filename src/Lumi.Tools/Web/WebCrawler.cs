using System.Globalization;
using System.Text;
using Lumi.Core.Browser;
using Lumi.Core.Web;

namespace Lumi.Tools.Web;

internal enum CrawlMode { Auto, Http, Browser }

/// <param name="OutputFolder">已經由工具經 context.Scope 解析過、確定在範圍內的資料夾完整路徑（引擎在它底下建新的子資料夾）。</param>
internal sealed record CrawlRequest(
    Uri Start, string OutputFolder, int MaxPages, int Depth,
    IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, CrawlMode Mode, string ConversationId);

internal sealed record CrawledPage(int Index, Uri Url, string? Title, string FileName, int Chars);

internal sealed record CrawlFailure(Uri Url, string Reason);

/// <param name="Folder">實際建立的子資料夾完整路徑。</param>
/// <param name="UsedBrowser">最後用的是瀏覽器還是 HTTP。</param>
/// <param name="Notes">給模型看的提示（例如「起始頁需要 JavaScript，但瀏覽器不可用，改用 HTTP」）。</param>
internal sealed record CrawlReport(
    string Folder, bool UsedBrowser, IReadOnlyList<CrawledPage> Pages, IReadOnlyList<CrawlFailure> Failures,
    int BlockedByRobots, IReadOnlyList<Uri> FileLinks, IReadOnlyList<string> Notes, bool Stopped);

/// <summary>爬取引擎（D1～D9）。網路只經過 <see cref="IWebClient"/> 與 <see cref="IBrowserSessions"/>；寫檔只寫在 <see cref="CrawlRequest.OutputFolder"/> 底下新建的子資料夾。</summary>
internal sealed class WebCrawler
{
    private const long RobotsMaxBytes = 512L * 1024;
    private const int MinReadableChars = 200;
    private const int FileLinkListLimit = 50;
    private const string BrowserSessionSuffix = "#crawl";
    private const string BrowserSwitchNote = "起始頁看起來需要登入或 JavaScript，但找不到可用的瀏覽器，改用 HTTP 讀取，內容可能不完整。";

    private readonly IWebClient _web;
    private readonly IWebContentExtractor _extractor;
    private readonly IBrowserSessions _browser;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="delay">兩頁之間的等待；測試傳入立即完成的委派。null＝真的等（用 <paramref name="time"/>）。</param>
    public WebCrawler(
        IWebClient web, IWebContentExtractor extractor, IBrowserSessions browser, TimeProvider time,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _web = web;
        _extractor = extractor;
        _browser = browser;
        _time = time;
        _delay = delay ?? ((d, ct) => Task.Delay(d, time, ct));
    }

    /// <summary>
    /// 起始頁讀不到（連線錯誤、HTTP ≥ 400 且不能改用瀏覽器、被 robots.txt 擋）丟 <see cref="CrawlException"/>，訊息可直接給模型；
    /// 之後的單頁失敗記在 <see cref="CrawlReport.Failures"/>。<paramref name="ct"/> 取消時回 <c>Stopped=true</c> 的報告（已存的保留），不丟例外。
    /// 例外：取消發生在建立輸出資料夾之前（robots.txt、起始頁的讀取階段）時還沒有任何結果可回報，照常丟 <see cref="OperationCanceledException"/>。
    /// </summary>
    public Task<CrawlReport> RunAsync(CrawlRequest request, IProgress<string>? progress, CancellationToken ct) =>
        new Run(this, request, progress).ExecuteAsync(ct);

    private enum ReadKind { Page, NotHtml, Failed }

    private sealed record PageContent(Uri FinalUrl, string? Title, string Markdown, IReadOnlyList<WebLink> Links, bool HasScript);

    private sealed record ReadResult(ReadKind Kind, PageContent? Content, string? Reason);

    private sealed record QueueItem(Uri Url, int Depth);

    /// <summary>一次爬取的狀態（每次 <see cref="RunAsync"/> 一個實例）。</summary>
    private sealed class Run(WebCrawler owner, CrawlRequest request, IProgress<string>? progress)
    {
        private readonly Uri _start = request.Start;
        private readonly List<CrawledPage> _pages = [];
        private readonly List<CrawlFailure> _failures = [];
        private readonly List<Uri> _fileLinks = [];
        private readonly List<string> _notes = [];
        private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
        private readonly Queue<QueueItem> _queue = new();
        private RobotsRules _robots = RobotsRules.AllowAll;
        private CrawlMode _mode;
        private ReadResult? _cachedStart;
        private string _folder = "";
        private int _blockedByRobots;

        private string SessionId => request.ConversationId + BrowserSessionSuffix;

        public async Task<CrawlReport> ExecuteAsync(CancellationToken ct)
        {
            _robots = await LoadRobotsAsync(ct);
            if (!_robots.IsAllowed(_start))
                throw new CrawlException("這個網站的 robots.txt 不允許爬取這個網址。");

            await DecideModeAsync(ct);

            _folder = CreateOutputFolder();
            _visited.Add(Canonical(_start));
            _queue.Enqueue(new QueueItem(_start, 0));

            var stopped = false;
            try
            {
                await CrawlAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stopped = true;
            }
            finally
            {
                if (_mode == CrawlMode.Browser)
                    await CloseBrowserAsync();
            }

            await WriteIndexAsync(stopped);
            return new CrawlReport(_folder, _mode == CrawlMode.Browser, _pages, _failures, _blockedByRobots, _fileLinks, _notes, stopped);
        }

        // ---------- robots.txt（D6）----------

        private async Task<RobotsRules> LoadRobotsAsync(CancellationToken ct)
        {
            try
            {
                var robotsRequest = new WebRequest(new Uri(_start, "/robots.txt")) { MaxBytes = RobotsMaxBytes };
                var response = await owner._web.SendAsync(robotsRequest, ct);
                if (response.StatusCode is < 200 or > 299)
                    return RobotsRules.AllowAll;
                if (response.MediaType is null or "text/plain")
                    return RobotsRules.Parse(owner._extractor.DecodeText(response.Body, response.Charset, false));
                return RobotsRules.AllowAll;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // robots.txt 讀不到（連線錯誤、逾時、格式錯誤）一律當作全部允許。
                return RobotsRules.AllowAll;
            }
        }

        // ---------- 決定讀法（D4）----------

        private async Task DecideModeAsync(CancellationToken ct)
        {
            if (request.Mode == CrawlMode.Browser)
            {
                var availability = owner._browser.CheckAvailability();
                if (!availability.IsAvailable)
                    throw new CrawlException(availability.Problem ?? "找不到可用的瀏覽器。");
                _mode = CrawlMode.Browser;
                return;
            }

            var response = await FetchStartAsync(ct);
            if (request.Mode == CrawlMode.Auto)
            {
                var first = ToReadResult(response);
                var needsBrowser = response.StatusCode is 401 or 403
                    || (first.Content is { HasScript: true } content && content.Markdown.Trim().Length < MinReadableChars);
                if (needsBrowser)
                {
                    if (owner._browser.CheckAvailability().IsAvailable)
                    {
                        _mode = CrawlMode.Browser;
                        return;
                    }
                    _notes.Add(BrowserSwitchNote);
                }
            }

            _mode = CrawlMode.Http;
            if (response.StatusCode >= 400)
                throw new CrawlException($"起始頁回應 {response.StatusCode} {response.ReasonPhrase}。");
            _cachedStart = ToReadResult(response);
        }

        private async Task<WebResponse> FetchStartAsync(CancellationToken ct)
        {
            try
            {
                return await owner._web.SendAsync(new WebRequest(_start), ct);
            }
            catch (WebRequestException e)
            {
                throw new CrawlException(e.Message);
            }
        }

        // ---------- 讀一頁（HTTP 或瀏覽器）----------

        private async Task<ReadResult> ReadAsync(Uri url, CancellationToken ct)
        {
            if (_mode == CrawlMode.Browser)
                return await ReadBrowserAsync(url, ct);

            // 起始頁在決定讀法時已經用 HTTP 讀過，直接重用，不重讀。
            if (_cachedStart is { } cached)
            {
                _cachedStart = null;
                return cached;
            }

            try
            {
                var response = await owner._web.SendAsync(new WebRequest(url), ct);
                return ToReadResult(response);
            }
            catch (WebRequestException e)
            {
                return new ReadResult(ReadKind.Failed, null, e.Message);
            }
        }

        private async Task<ReadResult> ReadBrowserAsync(Uri url, CancellationToken ct)
        {
            // 瀏覽器模式無法分辨非 HTML，一律當網頁處理。
            try
            {
                await owner._browser.OpenAsync(SessionId, url, ct);
                var content = await owner._browser.GetContentAsync(SessionId, null, ct);
                var page = owner._extractor.Extract(content.Html, content.Url, readable: true);
                return Page(new PageContent(content.Url, page.Title ?? content.Title, page.Markdown, page.Links, HasScript: false));
            }
            catch (BrowserException e)
            {
                return new ReadResult(ReadKind.Failed, null, e.Message);
            }
        }

        private ReadResult ToReadResult(WebResponse response)
        {
            if (response.StatusCode >= 400)
                return new ReadResult(ReadKind.Failed, null, $"HTTP {response.StatusCode}");
            if (response.MediaType is not ("text/html" or "application/xhtml+xml"))
                return new ReadResult(ReadKind.NotHtml, null, null);

            var html = owner._extractor.DecodeText(response.Body, response.Charset, true);
            var page = owner._extractor.Extract(html, response.FinalUrl, readable: true);
            var hasScript = html.Contains("<script", StringComparison.OrdinalIgnoreCase);
            return Page(new PageContent(response.FinalUrl, page.Title, page.Markdown, page.Links, hasScript));
        }

        private static ReadResult Page(PageContent content) => new(ReadKind.Page, content, null);

        // ---------- 佇列與主迴圈（D1、D2、D7）----------

        private async Task CrawlAsync(CancellationToken ct)
        {
            var readAny = false;
            while (_queue.Count > 0 && _pages.Count < request.MaxPages)
            {
                var item = _queue.Dequeue();
                if (!_robots.IsAllowed(item.Url))
                {
                    _blockedByRobots++;
                    continue;
                }

                if (readAny)
                    await owner._delay(CrawlRules.PageDelay, ct);
                readAny = true;

                progress?.Report($"第 {_pages.Count + 1}／{request.MaxPages} 頁");
                var result = await ReadAsync(item.Url, ct);
                switch (result.Kind)
                {
                    case ReadKind.Failed:
                        _failures.Add(new CrawlFailure(item.Url, result.Reason ?? "讀取失敗。"));
                        break;
                    case ReadKind.NotHtml:
                        _fileLinks.Add(item.Url);
                        break;
                    case ReadKind.Page:
                        await SavePageAsync(item, result.Content!, ct);
                        break;
                }
            }
        }

        private async Task SavePageAsync(QueueItem item, PageContent content, CancellationToken ct)
        {
            if (!CrawlRules.SameSite(_start, content.FinalUrl))
            {
                _failures.Add(new CrawlFailure(item.Url, "轉址到其他網站"));
                return;
            }

            var index = _pages.Count + 1;
            var fileName = CrawlRules.PageFileName(index, content.Title);
            var now = owner._time.GetLocalNow();
            var heading = DisplayTitle(content.Title, content.FinalUrl);
            var text = new StringBuilder()
                .Append("# ").Append(heading).Append("\n\n")
                .Append("來源：").Append(content.FinalUrl.AbsoluteUri).Append('\n')
                .Append("擷取時間：").Append(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\n\n")
                .Append("---\n\n")
                .Append(content.Markdown)
                .ToString();
            await WriteNewFileAsync(Path.Combine(_folder, fileName), text, ct);

            _pages.Add(new CrawledPage(index, content.FinalUrl, content.Title, fileName, content.Markdown.Length));
            EnqueueLinks(item, content.Links);
        }

        private void EnqueueLinks(QueueItem parent, IReadOnlyList<WebLink> links)
        {
            var childDepth = parent.Depth + 1;
            if (childDepth > request.Depth)
                return;

            foreach (var link in links)
            {
                var url = CrawlRules.Normalize(link.Url);
                if (url is null || !CrawlRules.SameSite(_start, url))
                    continue;
                if (!CrawlRules.PassesFilters(url, request.Include, request.Exclude))
                    continue;
                if (CrawlRules.IsLogoutLink(url, link.Text))
                    continue;
                if (!_visited.Add(url.AbsoluteUri))
                    continue;
                // 看副檔名就知道是檔案的連結不讀（瀏覽器模式讀不出差別，會把 PDF 檢視器的畫面存成網頁），只列在目錄裡
                if (CrawlRules.LooksLikeFile(url))
                {
                    _fileLinks.Add(url);
                    continue;
                }
                _queue.Enqueue(new QueueItem(url, childDepth));
            }
        }

        // ---------- 寫檔（D9）----------

        private string CreateOutputFolder()
        {
            var baseName = CrawlRules.FolderName(_start, owner._time.GetLocalNow());
            var candidate = Path.Combine(request.OutputFolder, baseName);
            for (var n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
                candidate = Path.Combine(request.OutputFolder, $"{baseName}-{n}");
            Directory.CreateDirectory(candidate);
            return candidate;
        }

        private async Task WriteIndexAsync(bool stopped)
        {
            var now = owner._time.GetLocalNow();
            var text = new StringBuilder();
            text.Append("# 爬取結果：").Append(_start.Host).Append("\n\n");
            text.Append("- 起始網址：").Append(_start.AbsoluteUri).Append('\n');
            text.Append("- 擷取時間：").Append(now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("- 讀法：").Append(_mode == CrawlMode.Browser ? "瀏覽器（使用 Lumi 瀏覽器的登入狀態）" : "HTTP").Append("\n\n");

            text.Append("## 頁面\n\n");
            if (_pages.Count == 0)
                text.Append("（沒有讀到任何頁面。）\n");
            foreach (var page in _pages)
                text.Append(page.Index).Append(". [").Append(DisplayTitle(page.Title, page.Url)).Append("](").Append(page.FileName)
                    .Append(") — ").Append(page.Url.AbsoluteUri).Append('\n');

            if (_blockedByRobots > 0)
                text.Append("\n被 robots.txt 擋下：").Append(_blockedByRobots).Append(" 個網址\n");

            if (_failures.Count > 0)
            {
                text.Append("\n## 讀取失敗\n\n");
                foreach (var failure in _failures)
                    text.Append("- ").Append(failure.Url.AbsoluteUri).Append("：").Append(failure.Reason).Append('\n');
            }

            if (_fileLinks.Count > 0)
            {
                text.Append("\n## 其他檔案連結\n\n");
                foreach (var link in _fileLinks.Take(FileLinkListLimit))
                    text.Append("- ").Append(link.AbsoluteUri).Append('\n');
            }

            if (stopped)
                text.Append("\n（使用者中途停止，只存了前 ").Append(_pages.Count).Append(" 頁。）\n");

            await WriteNewFileAsync(Path.Combine(_folder, "index.md"), text.ToString(), CancellationToken.None);
        }

        private static async Task WriteNewFileAsync(string path, string text, CancellationToken ct)
        {
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await stream.WriteAsync(bytes, ct);
        }

        private static string DisplayTitle(string? title, Uri url) =>
            string.IsNullOrWhiteSpace(title) ? url.AbsoluteUri : title.Trim();

        private static string Canonical(Uri url) => CrawlRules.Normalize(url)?.AbsoluteUri ?? url.AbsoluteUri;

        private async Task CloseBrowserAsync()
        {
            try
            {
                await owner._browser.CloseAsync(SessionId, CancellationToken.None);
            }
            catch (Exception)
            {
                // 關閉失敗不影響已經存下來的結果。
            }
        }
    }
}

internal sealed class CrawlException(string message) : Exception(message);
