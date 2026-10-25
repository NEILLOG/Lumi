using System.Net;
using System.Net.Http;
using System.Text;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Lumi.Web.Http;
using WebClient = Lumi.Web.Http.WebClient;
using WebRequest = Lumi.Core.Web.WebRequest;

namespace Lumi.Web.Tests.Http;

public sealed class WebClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lumi-webclient-" + Guid.NewGuid().ToString("N"));

    public WebClientTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed record Seen(HttpMethod Method, Uri Url, Dictionary<string, string> Headers, string? Body);

    private sealed class FuncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            headers["User-Agent"] = request.Headers.UserAgent.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers)
                    headers[h.Key] = string.Join(", ", h.Value);
            }
            Requests.Add(new Seen(request.Method, request.RequestUri!, headers, body));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Ok(string body = "ok", string mediaType = "text/plain") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static (WebClient Client, FuncHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond, int timeoutSeconds = 30)
    {
        var handler = new FuncHandler((r, _) => Task.FromResult(respond(r)));
        return (Make(handler, timeoutSeconds), handler);
    }

    private static WebClient Make(FuncHandler handler, int timeoutSeconds = 30) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new FakeWebCatalog(NetworkSettings.Default with { TimeoutSeconds = timeoutSeconds }),
            NullLogger<WebClient>.Instance);

    [Fact]
    public async Task Sends_default_headers()
    {
        var (client, handler) = Create(_ => Ok());

        await client.SendAsync(new WebRequest(new Uri("https://example.com/a")), CancellationToken.None);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.StartsWith("Mozilla/5.0 (compatible; Lumi/", seen.Headers["User-Agent"]);
        Assert.Contains("text/html", seen.Headers["Accept"]);
        Assert.StartsWith("zh-TW", seen.Headers["Accept-Language"]);
    }

    [Fact]
    public async Task Custom_headers_override_defaults_and_reserved_ones_are_ignored()
    {
        var (client, handler) = Create(_ => Ok());
        var request = new WebRequest(new Uri("https://example.com/"))
        {
            Headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Test"] = "1",
                ["Host"] = "evil.example",
                ["Proxy-Authorization"] = "Basic xxx",
            },
        };

        await client.SendAsync(request, CancellationToken.None);

        var headers = handler.Requests[0].Headers;
        Assert.Equal("application/json", headers["Accept"]);
        Assert.Equal("1", headers["X-Test"]);
        Assert.False(headers.ContainsKey("Host"));
        Assert.False(headers.ContainsKey("Proxy-Authorization"));
        Assert.StartsWith("Mozilla/5.0", headers["User-Agent"]);
    }

    [Fact]
    public async Task Follows_relative_and_absolute_redirects()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath switch
        {
            "/a/start" => Redirect(HttpStatusCode.Moved, "next"),
            "/a/next" => Redirect(HttpStatusCode.Found, "https://other.example/final?x=1"),
            _ => Ok("done"),
        });

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/a/start")), CancellationToken.None);

        Assert.Equal(["https://example.com/a/start", "https://example.com/a/next", "https://other.example/final?x=1"],
            handler.Requests.Select(r => r.Url.AbsoluteUri));
        Assert.Equal("https://other.example/final?x=1", response.FinalUrl.AbsoluteUri);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("done", Encoding.UTF8.GetString(response.Body));
    }

    [Fact]
    public async Task See_other_becomes_get_without_body()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath == "/post" ? Redirect(HttpStatusCode.SeeOther, "/result") : Ok());
        var request = new WebRequest(new Uri("https://example.com/post"), "POST") { Body = "{\"a\":1}" };

        await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("{\"a\":1}", handler.Requests[0].Body);
        Assert.Equal(HttpMethod.Get, handler.Requests[1].Method);
        Assert.Null(handler.Requests[1].Body);
    }

    [Fact]
    public async Task Temporary_redirect_keeps_method_and_body()
    {
        var (client, handler) = Create(r => r.RequestUri!.AbsolutePath == "/post" ? Redirect(HttpStatusCode.TemporaryRedirect, "/again") : Ok());
        var request = new WebRequest(new Uri("https://example.com/post"), "POST") { Body = "data", ContentType = "text/plain" };

        await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Requests[1].Method);
        Assert.Equal("data", handler.Requests[1].Body);
        Assert.StartsWith("text/plain", handler.Requests[1].Headers["Content-Type"]);
    }

    [Fact]
    public async Task Body_defaults_to_json_content_type()
    {
        var (client, handler) = Create(_ => Ok());

        await client.SendAsync(new WebRequest(new Uri("https://example.com/"), "POST") { Body = "{}" }, CancellationToken.None);

        Assert.StartsWith("application/json", handler.Requests[0].Headers["Content-Type"]);
    }

    [Fact]
    public async Task Authorization_is_dropped_when_redirected_to_another_host()
    {
        var (client, handler) = Create(r => r.RequestUri!.Host == "example.com" ? Redirect(HttpStatusCode.Found, "https://other.example/") : Ok());
        var request = new WebRequest(new Uri("https://example.com/"))
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret", ["X-Keep"] = "1" },
        };

        await client.SendAsync(request, CancellationToken.None);

        Assert.True(handler.Requests[0].Headers.ContainsKey("Authorization"));
        Assert.False(handler.Requests[1].Headers.ContainsKey("Authorization"));
        Assert.Equal("1", handler.Requests[1].Headers["X-Keep"]);
    }

    [Fact]
    public async Task Too_many_redirects_throws()
    {
        var (client, handler) = Create(_ => Redirect(HttpStatusCode.Found, "/loop"));

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None));

        Assert.Equal(WebFailureKind.TooManyRedirects, ex.Kind);
        Assert.Equal(WebLimits.MaxRedirects + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task Five_redirects_are_allowed()
    {
        var count = 0;
        var (client, _) = Create(_ => ++count <= WebLimits.MaxRedirects ? Redirect(HttpStatusCode.Found, "/next") : Ok());

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None);

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Redirect_to_unsupported_scheme_throws()
    {
        var (client, _) = Create(_ => Redirect(HttpStatusCode.Found, "ftp://example.com/file"));

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None));

        Assert.Equal(WebFailureKind.InvalidUrl, ex.Kind);
        Assert.Equal("伺服器轉址到不支援的網址。", ex.Message);
    }

    [Fact]
    public async Task Proxy_authentication_required_throws_proxy_authentication()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired));

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None));

        Assert.Equal(WebFailureKind.ProxyAuthentication, ex.Kind);
    }

    [Fact]
    public async Task Http_errors_are_returned_not_thrown()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("nope") });

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("Not Found", response.ReasonPhrase);
    }

    [Fact]
    public async Task Body_over_max_bytes_is_truncated()
    {
        var (client, _) = Create(_ => Ok(new string('a', 1000)));

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/")) { MaxBytes = 100 }, CancellationToken.None);

        Assert.True(response.Truncated);
        Assert.Equal(100, response.Body.Length);
    }

    [Fact]
    public async Task Body_exactly_max_bytes_is_not_truncated()
    {
        var (client, _) = Create(_ => Ok(new string('a', 100)));

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/")) { MaxBytes = 100 }, CancellationToken.None);

        Assert.False(response.Truncated);
        Assert.Equal(100, response.Body.Length);
    }

    [Fact]
    public async Task Parses_media_type_charset_and_headers()
    {
        var (client, _) = Create(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "Text/HTML; Charset=\"Big5\"");
            response.Headers.TryAddWithoutValidation("X-Custom", ["a", "b"]);
            return response;
        });

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None);

        Assert.Equal("text/html", response.MediaType);
        Assert.Equal("big5", response.Charset);
        Assert.Contains(response.Headers, h => h.Key == "x-custom" && h.Value == "a, b");
        Assert.Contains(response.Headers, h => h.Key == "content-type");
    }

    [Fact]
    public async Task Missing_content_type_gives_null_media_type_and_charset()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });

        var response = await client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None);

        Assert.Null(response.MediaType);
        Assert.Null(response.Charset);
    }

    [Fact]
    public async Task Slow_server_times_out()
    {
        var handler = new FuncHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok();
        });
        var client = Make(handler, timeoutSeconds: 1);

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None));

        Assert.Equal(WebFailureKind.Timeout, ex.Kind);
        Assert.Equal("連線逾時（1 秒）。", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_throws_operation_canceled()
    {
        var handler = new FuncHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Ok();
        });
        var client = Make(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(new WebRequest(new Uri("https://example.com/")), cts.Token));
    }

    [Fact]
    public async Task Connection_errors_become_web_request_exceptions()
    {
        var handler = new FuncHandler((_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "x"));
        var client = Make(handler);

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.SendAsync(new WebRequest(new Uri("https://example.com/")), CancellationToken.None));

        Assert.Equal(WebFailureKind.NameResolution, ex.Kind);
    }

    [Fact]
    public async Task Download_writes_file_via_temp_and_renames()
    {
        var (client, _) = Create(_ => Ok("hello", "text/plain"));
        var path = Path.Combine(_dir, "out.txt");

        var result = await client.DownloadAsync(new Uri("https://example.com/f"), path, 1000, CancellationToken.None);

        Assert.Equal("hello", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".lumi-download"));
        Assert.Equal(5, result.Bytes);
        Assert.Equal("text/plain", result.MediaType);
        Assert.Equal("https://example.com/f", result.FinalUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Download_follows_redirects_and_reports_final_url()
    {
        var (client, _) = Create(r => r.RequestUri!.AbsolutePath == "/f" ? Redirect(HttpStatusCode.Found, "/real") : Ok("x"));
        var path = Path.Combine(_dir, "out.bin");

        var result = await client.DownloadAsync(new Uri("https://example.com/f"), path, 1000, CancellationToken.None);

        Assert.Equal("https://example.com/real", result.FinalUrl.AbsoluteUri);
    }

    [Fact]
    public async Task Download_rejects_declared_content_length_over_limit()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        var path = Path.Combine(_dir, "big.bin");

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.DownloadAsync(new Uri("https://example.com/f"), path, 1024, CancellationToken.None));

        Assert.Equal(WebFailureKind.TooLarge, ex.Kind);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".lumi-download"));
    }

    [Fact]
    public async Task Download_message_uses_megabytes()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[3 * 1024 * 1024]) });
        var path = Path.Combine(_dir, "big.bin");

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.DownloadAsync(new Uri("https://example.com/f"), path, 2 * 1024 * 1024, CancellationToken.None));

        Assert.Equal(WebFailureKind.TooLarge, ex.Kind);
        Assert.Equal("檔案超過 2 MB，無法下載。", ex.Message);
    }

    [Fact]
    public async Task Download_over_limit_while_streaming_deletes_temp_file()
    {
        // 沒有 Content-Length 的串流：一路讀到超過上限才發現
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[5000])) });
        var path = Path.Combine(_dir, "stream.bin");

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.DownloadAsync(new Uri("https://example.com/f"), path, 1000, CancellationToken.None));

        Assert.Equal(WebFailureKind.TooLarge, ex.Kind);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".lumi-download"));
    }

    [Fact]
    public async Task Download_non_success_status_throws()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var path = Path.Combine(_dir, "missing.bin");

        var ex = await Assert.ThrowsAsync<WebRequestException>(() =>
            client.DownloadAsync(new Uri("https://example.com/f"), path, 1000, CancellationToken.None));

        Assert.Equal(WebFailureKind.Other, ex.Kind);
        Assert.Equal("伺服器回應 404 Not Found。", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Download_does_not_overwrite_existing_destination()
    {
        var (client, _) = Create(_ => Ok("new"));
        var path = Path.Combine(_dir, "exists.txt");
        await File.WriteAllTextAsync(path, "old");

        await Assert.ThrowsAsync<WebRequestException>(() =>
            client.DownloadAsync(new Uri("https://example.com/f"), path, 1000, CancellationToken.None));

        Assert.Equal("old", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".lumi-download"));
    }

    [Fact]
    public async Task Download_cancellation_deletes_temp_file()
    {
        var handler = new FuncHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[10], hang: true)) }));
        var client = Make(handler);
        var path = Path.Combine(_dir, "cancel.bin");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DownloadAsync(new Uri("https://example.com/f"), path, 1000, cts.Token));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".lumi-download"));
    }

    /// <summary>不能 Seek 的串流：<see cref="StreamContent"/> 因此不宣告長度（模擬 chunked）；<paramref name="hang"/> 為 true 時讀完資料後卡住直到取消。</summary>
    private sealed class NonSeekableStream(byte[] data, bool hang = false) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= data.Length)
            {
                if (hang)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
