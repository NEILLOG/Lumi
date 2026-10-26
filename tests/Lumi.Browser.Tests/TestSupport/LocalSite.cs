using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lumi.Browser.Tests.TestSupport;

/// <summary>
/// 測試用的本機網站（127.0.0.1、隨機 port）。路徑：<c>/</c> 測試頁、<c>/login</c> 設 Cookie、<c>/whoami</c> 回傳 Cookie 標頭、
/// <c>/dl</c> 下載 <c>a.txt</c>、<c>/slow</c> 等 2 秒才回、<c>/tall</c> 很長的頁面（捲動用，最底下有「底部連結」）、
/// <c>/form</c> 表單（下拉選單「城市」、檔案欄位「附件」、文字框「備註」、按鈕「送出表單」，送出後 <c>#result</c> 顯示結果）。
/// </summary>
public sealed class LocalSite : IDisposable
{
    private const string IndexHtml = """
        <!doctype html>
        <html lang="zh-TW"><head><meta charset="utf-8"><title>POC</title></head>
        <body>
          <h1>測試頁</h1>
          <button onclick="document.title='clicked'">送出</button>
          <input type="text" aria-label="姓名">
          <input type="password" aria-label="密碼">
          <a href="/whoami" target="_blank">新分頁</a>
          <a href="/dl">下載</a>
          <button onclick="alert('hi')">跳對話框</button>
          <iframe srcdoc="&lt;button&gt;框內按鈕&lt;/button&gt;"></iframe>
          <table><tr><td>a1</td><td>b1</td></tr><tr><td>a2</td><td>b2</td></tr></table>
        </body></html>
        """;

    private const string TallHtml = """
        <!doctype html>
        <html lang="zh-TW"><head><meta charset="utf-8"><title>tall</title></head>
        <body>
          <h1>很長的頁面</h1>
          <div style="height:3000px">空白</div>
          <a href="/">底部連結</a>
        </body></html>
        """;

    private const string FormHtml = """
        <!doctype html>
        <html lang="zh-TW"><head><meta charset="utf-8"><title>form</title></head>
        <body>
          <h1>表單</h1>
          <label>城市 <select aria-label="城市"><option value="tpe">台北</option><option value="khh">高雄</option></select></label>
          <label>附件 <input type="file" aria-label="附件"></label>
          <label>備註 <input type="text" aria-label="備註"></label>
          <button id="go">送出表單</button>
          <p id="result"></p>
          <script>
            document.getElementById('go').addEventListener('click', function () {
              var city = document.querySelector('select').value;
              var note = document.querySelector('input[type=text]').value;
              var file = document.querySelector('input[type=file]').files[0];
              document.getElementById('result').textContent = '送出：' + city + '|' + note + '|' + (file ? file.name : '');
            });
          </script>
        </body></html>
        """;

    private static string CrawlPage(string title, params string[] links)
    {
        var list = string.Concat(links.Select(l => $"<li><a href=\"{l.Split('|')[0]}\">{l.Split('|')[1]}</a></li>"));
        var filler = string.Concat(Enumerable.Repeat("這是爬取測試頁面的內文，用來讓內容超過兩百個字，避免被當成需要 JavaScript 的空白頁。", 6));
        return $"<!doctype html><html lang=\"zh-TW\"><head><meta charset=\"utf-8\"><title>{title}</title></head><body><article><h1>{title}</h1><p>{filler}</p><ul>{list}</ul></article></body></html>";
    }

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public LocalSite()
    {
        for (var attempt = 0; ; attempt++)
        {
            var port = FreePort();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                BaseUri = new Uri($"http://127.0.0.1:{port}/");
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                // port 剛好被別人佔走，換一個
            }
        }
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>每個收到的請求的路徑（含查詢字串），用來確認某些網址根本沒被請求過。</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public Uri BaseUri { get; }

    public Uri Url(string path) => new(BaseUri, path.TrimStart('/'));

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 監聽已關閉
        }
        _cts.Dispose();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var response = context.Response;
            Requests.Enqueue(context.Request.Url?.PathAndQuery ?? "");
            switch (context.Request.Url?.AbsolutePath)
            {
                case "/":
                    await WriteAsync(response, "text/html; charset=utf-8", IndexHtml).ConfigureAwait(false);
                    break;
                case "/robots.txt":
                    await WriteAsync(response, "text/plain", "User-agent: *\nDisallow: /crawl/private\n").ConfigureAwait(false);
                    break;
                case "/crawl/":
                    await WriteAsync(response, "text/html; charset=utf-8", CrawlPage("爬取首頁",
                        "/crawl/a|頁面 A", "/crawl/b|頁面 B", "/crawl/logout|登出", "/crawl/private|私有頁", "/crawl/file.pdf|說明文件",
                        "http://other.invalid/x|外部網站")).ConfigureAwait(false);
                    break;
                case "/crawl/a":
                    await WriteAsync(response, "text/html; charset=utf-8", CrawlPage("頁面 A", "/crawl/c|頁面 C", "/crawl/|回首頁")).ConfigureAwait(false);
                    break;
                case "/crawl/b":
                    await WriteAsync(response, "text/html; charset=utf-8", CrawlPage("頁面 B")).ConfigureAwait(false);
                    break;
                case "/crawl/c":
                    await WriteAsync(response, "text/html; charset=utf-8", CrawlPage("頁面 C")).ConfigureAwait(false);
                    break;
                case "/crawl/private":
                    await WriteAsync(response, "text/html; charset=utf-8", CrawlPage("私有頁")).ConfigureAwait(false);
                    break;
                case "/crawl/logout":
                    await WriteAsync(response, "text/html; charset=utf-8", CrawlPage("登出")).ConfigureAwait(false);
                    break;
                case "/crawl/file.pdf":
                    await WriteAsync(response, "application/pdf", "%PDF-1.4 fake").ConfigureAwait(false);
                    break;
                case "/tall":
                    await WriteAsync(response, "text/html; charset=utf-8", TallHtml).ConfigureAwait(false);
                    break;
                case "/form":
                    await WriteAsync(response, "text/html; charset=utf-8", FormHtml).ConfigureAwait(false);
                    break;
                case "/login":
                    response.Headers.Add("Set-Cookie", "sid=abc123; Max-Age=3600; Path=/");
                    await WriteAsync(response, "text/html; charset=utf-8", "<!doctype html><title>login</title><p>ok</p>").ConfigureAwait(false);
                    break;
                case "/whoami":
                    var cookie = WebUtility.HtmlEncode(context.Request.Headers["Cookie"] ?? "");
                    await WriteAsync(response, "text/html; charset=utf-8",
                        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>whoami</title></head><body><p id=c>{cookie}</p></body></html>").ConfigureAwait(false);
                    break;
                case "/dl":
                    response.Headers.Add("Content-Disposition", "attachment; filename=a.txt");
                    await WriteAsync(response, "text/plain", "hello").ConfigureAwait(false);
                    break;
                case "/slow":
                    await Task.Delay(2000, _cts.Token).ConfigureAwait(false);
                    await WriteAsync(response, "text/html; charset=utf-8", "<!doctype html><title>slow</title><p>slow</p>").ConfigureAwait(false);
                    break;
                default:
                    response.StatusCode = 404;
                    await WriteAsync(response, "text/plain", "not found").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception)
        {
            // 瀏覽器中途斷線（關閉分頁）時寫回應會失敗，不影響測試
            try
            {
                context.Response.Abort();
            }
            catch (Exception)
            {
                // 已經關閉
            }
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}
