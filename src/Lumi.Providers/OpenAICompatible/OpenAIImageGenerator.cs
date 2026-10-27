using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Images;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace Lumi.Providers.OpenAICompatible;

/// <summary>
/// OpenAI 相容的圖片生成與編輯（22F）：<c>POST {base}/images/generations</c>（JSON）與 <c>POST {base}/images/edits</c>（multipart）。
/// 只有 OpenAI 相容格式的連線支援；Anthropic 格式會回 <see cref="ChatFailureKind.BadRequest"/> 的說明。
/// 有金鑰才送 <c>Authorization: Bearer</c>，金鑰每次呼叫才從 <see cref="ISecretStore"/> 讀。
/// </summary>
public sealed class OpenAIImageGenerator(HttpClient httpClient, ISecretStore secrets, ILogger<OpenAIImageGenerator> logger) : IImageGenerator
{
    private const string FallbackMediaType = "image/png";

    public async Task<GeneratedImage> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        var connection = EnsureOpenAI(request.Connection);
        var baseUri = OpenAIUrls.BaseUri(connection.BaseUrl);
        var apiKey = await ReadApiKeyAsync(connection, ct).ConfigureAwait(false);

        using var message = new HttpRequestMessage(HttpMethod.Post, OpenAIUrls.Endpoint(baseUri, "images/generations"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = request.ModelId,
                prompt = request.Prompt,
                size = request.Size,
                n = 1,
            }), Encoding.UTF8, "application/json"),
        };
        return await SendAsync(message, baseUri, connection, apiKey, ct).ConfigureAwait(false);
    }

    public async Task<GeneratedImage> EditAsync(ImageEditRequest request, CancellationToken ct = default)
    {
        var connection = EnsureOpenAI(request.Connection);
        var baseUri = OpenAIUrls.BaseUri(connection.BaseUrl);
        var apiKey = await ReadApiKeyAsync(connection, ct).ConfigureAwait(false);

        using var form = new MultipartFormDataContent
        {
            { new StringContent(request.ModelId), "model" },
            { new StringContent(request.Prompt), "prompt" },
            { new StringContent(request.Size), "size" },
            { new StringContent("1"), "n" },
        };
        foreach (var image in request.Images)
        {
            var part = new ByteArrayContent(image.Data);
            part.Headers.ContentType = new MediaTypeHeaderValue(image.MediaType);
            form.Add(part, "image[]", image.FileName);
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, OpenAIUrls.Endpoint(baseUri, "images/edits")) { Content = form };
        return await SendAsync(message, baseUri, connection, apiKey, ct).ConfigureAwait(false);
    }

    private static ConnectionSettings EnsureOpenAI(ConnectionSettings connection) =>
        connection.Format == ProviderFormat.OpenAICompatible
            ? connection
            : throw new ImageGenerationException(ChatFailureKind.BadRequest, "圖片生成需要 OpenAI 相容格式的連線，請到設定改選這類連線。");

    private async Task<string?> ReadApiKeyAsync(ConnectionSettings connection, CancellationToken ct)
    {
        try
        {
            return (await secrets.GetAsync(ConnectionSecrets.ApiKey(connection.Id), ct).ConfigureAwait(false))?.Reveal();
        }
        catch (SecretStoreException ex)
        {
            logger.LogWarning(ex, "讀取連線 {ConnectionId} 的金鑰失敗", connection.Id);
            throw new ImageGenerationException(ChatFailureKind.SecretUnreadable, "金鑰無法讀取，請重新輸入。");
        }
    }

    private async Task<GeneratedImage> SendAsync(
        HttpRequestMessage message, Uri baseUri, ConnectionSettings connection, string? apiKey, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await SendOrFailAsync(message, baseUri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var failure = OpenAIHttpErrors.FromResponse(response);
            logger.LogWarning("圖片生成（連線 {ConnectionId}）回應 HTTP {Status}", connection.Id, (int)response.StatusCode);
            throw new ImageGenerationException(failure.Kind, failure.Message);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (base64, url, revised) = ParseFirstImage(body);
        byte[] data;
        if (base64 is not null)
        {
            data = DecodeBase64(base64);
        }
        else if (url is not null)
        {
            // 有些伺服器回網址而不是 base64：同一個 client 再下載一次（網址是伺服器給的，不附金鑰）
            data = await DownloadAsync(url, ct).ConfigureAwait(false);
        }
        else
        {
            throw new ImageGenerationException(ChatFailureKind.Unknown, "伺服器沒有回傳圖片。");
        }

        return new GeneratedImage(data, ImageFormats.Detect(data) ?? FallbackMediaType, revised);
    }

    private async Task<HttpResponseMessage> SendOrFailAsync(HttpRequestMessage message, Uri baseUri, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failure = OpenAIHttpErrors.FromException(ex, baseUri);
            logger.LogWarning(ex, "圖片生成的連線失敗");
            throw new ImageGenerationException(failure.Kind, failure.Message);
        }
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ImageGenerationException(ChatFailureKind.Unknown, "伺服器回傳的圖片網址不正確。");

        using var response = await SendOrFailAsync(new HttpRequestMessage(HttpMethod.Get, uri), uri, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageGenerationException(ChatFailureKind.Unknown, $"下載生成的圖片失敗（HTTP {(int)response.StatusCode}）。");
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>取 <c>data[0]</c> 的 <c>b64_json</c> 或 <c>url</c>，以及 <c>revised_prompt</c>；格式不對時都回 null。</summary>
    private static (string? Base64, string? Url, string? Revised) ParseFirstImage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                return (null, null, null);
            var first = data[0];
            return (ReadString(first, "b64_json"), ReadString(first, "url"), ReadString(first, "revised_prompt"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static byte[] DecodeBase64(string base64)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new ImageGenerationException(ChatFailureKind.Unknown, "伺服器回傳的圖片資料格式不正確。");
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
