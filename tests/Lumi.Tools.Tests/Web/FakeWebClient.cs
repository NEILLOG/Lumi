using System.Text;
using Lumi.Core.Web;

namespace Lumi.Tools.Tests.Web;

/// <summary>記錄請求、回傳預先設定的回應或丟出例外。</summary>
internal sealed class FakeWebClient : IWebClient
{
    public List<WebRequest> Requests { get; } = [];

    public List<(Uri Url, string Path, long MaxBytes)> Downloads { get; } = [];

    public Func<WebRequest, WebResponse> Respond { get; set; } = _ => Html("<html><body><p>ok</p></body></html>");

    public Func<Uri, string, long, WebDownloadResult> Download { get; set; } = (url, _, _) => new WebDownloadResult(url, 10, "application/octet-stream");

    public Exception? Throw { get; set; }

    public Task<WebResponse> SendAsync(WebRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Respond(request));
    }

    public Task<WebDownloadResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct)
    {
        Downloads.Add((url, destinationPath, maxBytes));
        if (Throw is not null)
            throw Throw;
        return Task.FromResult(Download(url, destinationPath, maxBytes));
    }

    public static WebResponse Html(string html, string url = "https://example.com/", int status = 200, bool truncated = false) =>
        Text("text/html", html, url, status, truncated);

    public static WebResponse Text(string mediaType, string body, string url = "https://example.com/", int status = 200, bool truncated = false) =>
        new(status, status < 400 ? "OK" : "Error", new Uri(url), mediaType, "utf-8", [], Encoding.UTF8.GetBytes(body), truncated);

    public static WebResponse Bytes(string mediaType, byte[] body, string url = "https://example.com/file", int status = 200, bool truncated = false) =>
        new(status, "OK", new Uri(url), mediaType, null, [], body, truncated);
}
