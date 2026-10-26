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
    /// </summary>
    public Task<CrawlReport> RunAsync(CrawlRequest request, IProgress<string>? progress, CancellationToken ct) => throw new NotImplementedException();
}

internal sealed class CrawlException(string message) : Exception(message);
