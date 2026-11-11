using System.Net.Http.Headers;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.Providers.OpenAICompatible;

/// <summary>
/// 列出模型：<c>GET {BaseUrl}/models</c>。伺服器回 404／405 表示連得上、但沒有這個端點（例如只做圖片生成的服務），
/// 算成功（<see cref="ConnectionTestResult.NoModelList"/>），模型 ID 手動輸入（FX-06）。網路不通、憑證、401／403、5xx 才算失敗。
/// </summary>
public sealed class OpenAICompatibleConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;

    public OpenAICompatibleConnectionTester(HttpClient httpClient, ISecretStore secrets)
    {
        _httpClient = httpClient;
        _secrets = secrets;
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectionSettings connection, SecretValue? apiKey, CancellationToken ct)
    {
        var baseUri = OpenAIUrls.BaseUri(connection.BaseUrl);
        try
        {
            var key = apiKey ?? await _secrets.GetAsync(ConnectionSecrets.ApiKey(connection.Id), ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, OpenAIUrls.Endpoint(baseUri, "models"));
            if (key is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Reveal());

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
                return ConnectionTestResult.NoModelList();
            if (!response.IsSuccessStatusCode)
                return ConnectionTestResult.Fail(OpenAIHttpErrors.FromResponse(response));

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            return ConnectionTestResult.Ok(ParseModelIds(doc.RootElement));
        }
        catch (SecretStoreException)
        {
            return ConnectionTestResult.Fail(new ChatFailure(ChatFailureKind.SecretUnreadable, "金鑰無法讀取，請重新輸入。", false));
        }
        catch (JsonException)
        {
            return ConnectionTestResult.Fail(new ChatFailure(ChatFailureKind.Unknown, "伺服器回應的模型清單格式無法辨識。", false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ConnectionTestResult.Fail(OpenAIHttpErrors.FromException(ex, baseUri));
        }
    }

    /// <summary>標準格式是 <c>{"data":[{"id":"..."}]}</c>；也容許直接是陣列。</summary>
    internal static IReadOnlyList<string> ParseModelIds(JsonElement root)
    {
        var items = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : default;
        if (items.ValueKind != JsonValueKind.Array)
            return [];

        return items.EnumerateArray()
            .Select(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
