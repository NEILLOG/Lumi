using System.Net;
using System.Text;

namespace Lumi.Providers.Tests.TestSupport;

/// <summary>
/// 假的圖片 API（22F）：記錄每次請求的方法、網址、Authorization 標頭與內容（以 Latin-1 解碼，方便比對 multipart 的 ASCII 欄位），
/// 回應由 <paramref name="respond"/> 決定。不送出任何真實請求（agent.md 第 15 節）。
/// </summary>
internal sealed class FakeImageHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Sent(HttpMethod Method, Uri Url, string? Authorization, string? ContentType, string Body);

    public List<Sent> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new Sent(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Content?.Headers.ContentType?.ToString(),
            Encoding.Latin1.GetString(bytes)));
        await Task.Yield();
        return respond(request);
    }

    /// <summary>JSON 回應（HTTP 200）。</summary>
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>二進位回應（例如網址的圖片）。</summary>
    public static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
}
