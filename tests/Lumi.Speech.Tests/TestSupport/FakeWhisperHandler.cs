using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Lumi.Speech.Tests.TestSupport;

/// <summary>
/// 假的網路層（agent.md 第 15 節）：不送出真實請求。每次請求都先把內容完整讀下來，記在 <see cref="Requests"/>，
/// 測試可以檢查表單欄位、標頭與位元組。
/// </summary>
internal sealed class FakeWhisperHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

    private FakeWhisperHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public List<CapturedRequest> Requests { get; } = [];

    public int CallCount => Requests.Count;

    public CapturedRequest Last => Requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new CapturedRequest(
            request.RequestUri!,
            request.Headers.Authorization,
            request.Headers.Accept.Select(a => a.MediaType ?? "").ToList(),
            request.Content?.Headers.ContentType?.ToString(),
            body));
        return await _respond(request, cancellationToken).ConfigureAwait(false);
    }

    public static FakeWhisperHandler Json(HttpStatusCode status, string json) => Reply(status, "application/json", json);

    public static FakeWhisperHandler Reply(HttpStatusCode status, string? contentType, string body) => new((_, _) =>
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)),
        };
        if (contentType is not null)
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return Task.FromResult(response);
    });

    public static FakeWhisperHandler Throws(Exception ex) => new((_, _) => throw ex);

    /// <summary>永遠不回應，直到請求被取消（用來測逾時與取消）。</summary>
    public static FakeWhisperHandler Hangs() => new(async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        throw new InvalidOperationException("不應該走到這裡");
    });
}

internal sealed record CapturedRequest(
    Uri Url,
    AuthenticationHeaderValue? Authorization,
    IReadOnlyList<string> Accept,
    string? ContentType,
    byte[] Body)
{
    public MultipartForm Form => MultipartForm.Parse(Body, ContentType!);
}
