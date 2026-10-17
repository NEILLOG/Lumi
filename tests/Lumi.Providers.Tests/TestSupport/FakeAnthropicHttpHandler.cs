using System.Net;
using System.Net.Http.Headers;

namespace Lumi.Providers.Tests.TestSupport;

/// <summary>
/// 假的網路層：不送出任何真實請求（agent.md 第 15 節），回傳預先準備好的 SSE 或 HTTP 錯誤內容。
/// 記錄最後一次請求，供測試檢查送出的 JSON 與標頭。
/// </summary>
internal sealed class FakeAnthropicHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    private FakeAnthropicHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        return _respond(request);
    }

    public static FakeAnthropicHttpHandler ForSse(byte[] sseBytes, int chunkSize) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ChunkedMemoryStream(sseBytes, chunkSize))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
        },
    });

    public static FakeAnthropicHttpHandler ForError(HttpStatusCode status, byte[] jsonBytes, TimeSpan? retryAfter = null) => new(_ =>
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(jsonBytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        if (retryAfter is { } ra)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        return response;
    });
}
