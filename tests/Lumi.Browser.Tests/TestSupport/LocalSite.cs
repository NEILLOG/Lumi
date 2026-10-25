using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Lumi.Browser.Tests.TestSupport;

/// <summary>
/// 測試用的本機網站（127.0.0.1、隨機 port）。路徑：<c>/</c> 測試頁、<c>/login</c> 設 Cookie、<c>/whoami</c> 回傳 Cookie 標頭、
/// <c>/dl</c> 下載 <c>a.txt</c>、<c>/slow</c> 等 2 秒才回。
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
            switch (context.Request.Url?.AbsolutePath)
            {
                case "/":
                    await WriteAsync(response, "text/html; charset=utf-8", IndexHtml).ConfigureAwait(false);
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
