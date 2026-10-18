using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace Lumi.Providers.OpenAICompatible;

/// <summary>
/// OpenAI 相容格式（OpenAI、Ollama、公司 Gateway）的 <see cref="IChatProvider"/>：自己寫的 HttpClient +
/// .NET 內建的 SSE 解析，只走 Chat Completions（M2 的 Q2）。有金鑰才送 <c>Authorization: Bearer</c>
/// （Ollama 可以沒有金鑰）。金鑰每次呼叫才從 <see cref="ISecretStore"/> 讀，不快取。
/// </summary>
public sealed class OpenAICompatibleChatProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly ILogger<OpenAICompatibleChatProvider> _logger;

    public OpenAICompatibleChatProvider(HttpClient httpClient, ISecretStore secrets, ILogger<OpenAICompatibleChatProvider> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var connection = request.Model.Connection;
        var baseUri = OpenAIUrls.BaseUri(connection.BaseUrl);

        string? apiKey = null;
        ChatFailure? secretFailure = null;
        try
        {
            apiKey = (await _secrets.GetAsync(ConnectionSecrets.ApiKey(connection.Id), ct).ConfigureAwait(false))?.Reveal();
        }
        catch (SecretStoreException ex)
        {
            _logger.LogWarning(ex, "讀取連線 {ConnectionId} 的金鑰失敗", connection.Id);
            secretFailure = new ChatFailure(ChatFailureKind.SecretUnreadable, "金鑰無法讀取，請重新輸入。", Retryable: false);
        }

        if (secretFailure is not null)
        {
            yield return new Failed(secretFailure);
            yield break;
        }

        var accumulator = new OpenAIStreamAccumulator(request.Model.Model.ModelId);
        HttpResponseMessage? response = null;
        IAsyncEnumerator<SseItem<string>>? enumerator = null;
        try
        {
            while (true)
            {
                List<ChatEvent>? produced = null;
                ChatFailure? failure = null;

                try
                {
                    if (enumerator is null)
                    {
                        using var httpRequest = BuildRequest(request, baseUri, apiKey);
                        response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            failure = OpenAIHttpErrors.FromResponse(response);
                            _logger.LogWarning("OpenAI 相容連線 {ConnectionId} 回應 HTTP {Status}", connection.Id, (int)response.StatusCode);
                        }
                        else
                        {
                            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                            enumerator = SseParser.Create(stream).EnumerateAsync(ct).GetAsyncEnumerator(ct);
                        }
                    }

                    if (failure is null && enumerator is not null)
                    {
                        produced = await enumerator.MoveNextAsync().ConfigureAwait(false)
                            ? accumulator.Process(enumerator.Current.Data)
                            : accumulator.Finish();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = enumerator is null
                        ? OpenAIHttpErrors.FromException(ex, baseUri)
                        : new ChatFailure(ChatFailureKind.StreamError, "與伺服器的連線中斷。", Retryable: true);
                    _logger.LogWarning(ex, "OpenAI 相容串流失敗（連線 {ConnectionId}）", connection.Id);
                }

                if (produced is not null)
                {
                    foreach (var chatEvent in produced)
                        yield return chatEvent;
                }

                if (failure is not null)
                {
                    yield return new Failed(failure);
                    yield break;
                }

                if (accumulator.IsFinished)
                    yield break;
            }
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
            response?.Dispose();
        }
    }

    private static HttpRequestMessage BuildRequest(ChatRequest request, Uri baseUri, string? apiKey)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, OpenAIUrls.Endpoint(baseUri, "chat/completions"))
        {
            Content = new StringContent(BuildBody(request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    /// <summary>
    /// 只送文字：thinking 區塊與其他供應商的區塊都略過（沒有 signature 的推理內容不能送回），
    /// 略過後沒有文字的訊息整則不送。
    /// </summary>
    internal static string BuildBody(ChatRequest request)
    {
        var messages = request.Messages
            .Select(m => (Role: m.Role == ChatRole.User ? "user" : "assistant", Text: string.Concat(m.Content.OfType<TextBlock>().Select(b => b.Text))))
            .Where(m => m.Text.Length > 0)
            .Select(m => new { role = m.Role, content = m.Text })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            model = request.Model.Model.ModelId,
            messages,
            stream = true,
            stream_options = new { include_usage = true },
            max_tokens = request.Model.Model.MaxOutputTokens,
        });
    }
}

internal static class OpenAIUrls
{
    public static Uri BaseUri(string baseUrl) => new(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    public static Uri Endpoint(Uri baseUri, string path) => new(baseUri, path);
}
