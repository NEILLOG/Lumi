using System.Diagnostics;
using System.Net;
using System.Text;
using Lumi.Core.Models;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;
using WebRequest = Lumi.Core.Web.WebRequest;
using WebResponse = Lumi.Core.Web.WebResponse;

namespace Lumi.Web.Http;

/// <summary>
/// 網路工具用的 HTTP 用戶端（M17）。<see cref="HttpClient"/> 由整合包設定好 Proxy、不自動轉址、不存 Cookie；
/// 這裡自己處理轉址、大小上限與逾時，log 只寫方法、網址（不含查詢字串）、狀態碼與耗時（D11）。
/// </summary>
internal sealed class WebClient(HttpClient http, IModelCatalog catalog, ILogger<WebClient> logger) : IWebClient
{
    /// <summary>下載的逾時下限（秒）：大檔案不該被網頁用的逾時切斷。</summary>
    private const int DownloadMinTimeoutSeconds = 300;

    private static readonly string UserAgent = $"Mozilla/5.0 (compatible; Lumi/{VersionText()})";

    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection",
    };

    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie",
    };

    public async Task<WebResponse> SendAsync(WebRequest request, CancellationToken ct)
    {
        var timeoutSeconds = Math.Max(1, catalog.Current.Network.TimeoutSeconds);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var (response, finalUrl) = await SendFollowingRedirectsAsync(request, timeout.Token).ConfigureAwait(false);
                using (response)
                {
                    var (body, truncated) = await ReadBodyAsync(response, request.MaxBytes, timeout.Token).ConfigureAwait(false);
                    var result = new WebResponse(
                        (int)response.StatusCode,
                        response.ReasonPhrase ?? "",
                        finalUrl,
                        MediaTypeOf(response),
                        CharsetOf(response),
                        CollectHeaders(response),
                        body,
                        truncated);
                    logger.LogInformation("網路請求 {Method} {Url} → {Status}（{Ms} ms）",
                        request.Method, request.Url.GetLeftPart(UriPartial.Path), result.StatusCode, stopwatch.ElapsedMilliseconds);
                    return result;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw ToWebException(e, timeout, timeoutSeconds);
            }
        }
        catch (WebRequestException e)
        {
            logger.LogWarning("網路請求 {Method} {Url} 失敗：{Message}", request.Method, request.Url.GetLeftPart(UriPartial.Path), e.Message);
            throw;
        }
    }

    public async Task<WebDownloadResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct)
    {
        var timeoutSeconds = Math.Max(catalog.Current.Network.TimeoutSeconds, DownloadMinTimeoutSeconds);
        var tempPath = destinationPath + ".lumi-download";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var (response, finalUrl) = await SendFollowingRedirectsAsync(new WebRequest(url), timeout.Token).ConfigureAwait(false);
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                        throw new WebRequestException(WebFailureKind.Other, $"伺服器回應 {(int)response.StatusCode} {response.ReasonPhrase}。");
                    if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
                        throw TooLarge(maxBytes);

                    var bytes = await WriteTempFileAsync(response, tempPath, maxBytes, timeout.Token).ConfigureAwait(false);
                    MoveIntoPlace(tempPath, destinationPath);
                    logger.LogInformation("網路請求 {Method} {Url} → {Status}（{Ms} ms）",
                        "GET", url.GetLeftPart(UriPartial.Path), (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
                    return new WebDownloadResult(finalUrl, bytes, MediaTypeOf(response));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                DeleteQuietly(tempPath);
                throw ToWebException(e, timeout, timeoutSeconds);
            }
            catch (OperationCanceledException)
            {
                DeleteQuietly(tempPath);
                throw;
            }
        }
        catch (WebRequestException e)
        {
            logger.LogWarning("網路請求 {Method} {Url} 失敗：{Message}", "GET", url.GetLeftPart(UriPartial.Path), e.Message);
            throw;
        }
    }

    /// <summary>把任何例外轉成給使用者看的 <see cref="WebRequestException"/>；本機逾時（不是呼叫端取消）一律算逾時。</summary>
    private static WebRequestException ToWebException(Exception e, CancellationTokenSource timeout, int timeoutSeconds) =>
        e is OperationCanceledException && timeout.IsCancellationRequested
            ? WebFailures.From(new TimeoutException(), timeoutSeconds)
            : WebFailures.From(e, timeoutSeconds);

    /// <summary>送出請求並自己處理轉址；回傳的回應由呼叫端負責釋放（轉址途中的回應在這裡釋放）。</summary>
    private async Task<(HttpResponseMessage Response, Uri FinalUrl)> SendFollowingRedirectsAsync(WebRequest request, CancellationToken ct)
    {
        var url = request.Url;
        var method = request.Method.ToUpperInvariant();
        var body = request.Body;
        var headers = request.Headers;
        for (var redirects = 0; ; redirects++)
        {
            using var message = BuildMessage(url, method, body, request.ContentType, headers);
            var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
            {
                response.Dispose();
                throw WebFailures.ProxyAuthentication();
            }

            var location = response.Headers.Location;
            if (!IsRedirect(response.StatusCode) || location is null)
                return (response, url);

            response.Dispose();
            if (redirects >= WebLimits.MaxRedirects)
                throw new WebRequestException(WebFailureKind.TooManyRedirects, $"轉址太多次（超過 {WebLimits.MaxRedirects} 次）。");

            var next = location.IsAbsoluteUri ? location : new Uri(url, location);
            if (next.Scheme is not ("http" or "https"))
                throw new WebRequestException(WebFailureKind.InvalidUrl, "伺服器轉址到不支援的網址。");

            // 301／302／303 之後改用 GET 且不帶內容（HEAD 維持 HEAD）；307／308 保留方法與內容
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther)
            {
                if (method != "HEAD")
                    method = "GET";
                body = null;
            }

            // 轉到別的主機時不把驗證用的標頭帶過去
            if (!string.Equals(next.Authority, url.Authority, StringComparison.OrdinalIgnoreCase))
                headers = headers.Where(h => !CredentialHeaders.Contains(h.Key)).ToDictionary(h => h.Key, h => h.Value);
            url = next;
        }
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage BuildMessage(Uri url, string method, string? body, string? contentType, IReadOnlyDictionary<string, string> custom)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
            message.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = UserAgent,
            ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            ["Accept-Language"] = "zh-TW,zh;q=0.9,en;q=0.8",
        };
        foreach (var (name, value) in custom)
        {
            if (IgnoredHeaders.Contains(name) || name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                continue;
            merged[name] = value;
        }

        foreach (var (name, value) in merged)
        {
            if (!message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        return message;
    }

    /// <summary>串流讀取，讀到 <paramref name="maxBytes"/> 就停止並標記截斷。</summary>
    private static async Task<(byte[] Body, bool Truncated)> ReadBodyAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
                return (buffer.ToArray(), false);
            var remaining = maxBytes - buffer.Length;
            if (read > remaining)
            {
                buffer.Write(chunk, 0, (int)remaining);
                return (buffer.ToArray(), true);
            }
            buffer.Write(chunk, 0, read);
        }
    }

    private static async Task<long> WriteTempFileAsync(HttpResponseMessage response, string tempPath, long maxBytes, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            long total = 0;
            await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var chunk = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > maxBytes)
                        throw TooLarge(maxBytes);
                    await file.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            return total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WebRequestException(WebFailureKind.Other, "無法寫入檔案。");
        }
    }

    private static void MoveIntoPlace(string tempPath, string destinationPath)
    {
        try
        {
            File.Move(tempPath, destinationPath, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new WebRequestException(WebFailureKind.Other,
                File.Exists(destinationPath) ? "目的地已經有同名檔案，沒有覆蓋。" : "無法寫入檔案。");
        }
    }

    private static WebRequestException TooLarge(long maxBytes) =>
        new(WebFailureKind.TooLarge, $"檔案超過 {maxBytes / (1024 * 1024)} MB，無法下載。");

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 暫存檔刪不掉不影響結果
        }
    }

    private static string? MediaTypeOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();

    private static string? CharsetOf(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.CharSet?.Trim('"').ToLowerInvariant();

    private static List<KeyValuePair<string, string>> CollectHeaders(HttpResponseMessage response)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            var name = header.Key.ToLowerInvariant();
            var value = string.Join(", ", header.Value);
            merged[name] = merged.TryGetValue(name, out var existing) ? existing + ", " + value : value;
        }
        return [.. merged];
    }

    private static string VersionText()
    {
        var version = typeof(WebClient).Assembly.GetName().Version ?? new Version(1, 0, 0);
        return $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }
}
