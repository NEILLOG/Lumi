using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Context;
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
    private readonly IAttachmentStore _attachments;

    public OpenAICompatibleChatProvider(
        HttpClient httpClient, ISecretStore secrets, ILogger<OpenAICompatibleChatProvider> logger, IAttachmentStore? attachments = null)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _logger = logger;
        _attachments = attachments ?? new NullAttachmentStore();
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

        var images = await ImageAttachments.LoadAsync(_attachments, request.Messages, ct).ConfigureAwait(false);
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
                        using var httpRequest = BuildRequest(request, baseUri, apiKey, images);
                        response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            failure = OpenAIHttpErrors.FromResponse(response, request.Tools is { Count: > 0 }, ImageAttachments.HasImages(request));
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

    private static HttpRequestMessage BuildRequest(ChatRequest request, Uri baseUri, string? apiKey, IReadOnlyDictionary<string, byte[]> images)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, OpenAIUrls.Endpoint(baseUri, "chat/completions"))
        {
            Content = new StringContent(BuildBody(request, images), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(apiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return message;
    }

    /// <summary>
    /// 歷史轉成 Chat Completions 格式：文字、<c>tool_calls</c>（助理）、<c>role: tool</c>（每個工具結果一則）都送；
    /// thinking 與其他供應商的區塊略過（沒有 signature 的推理內容不能送回），略過後什麼都不剩的訊息整則不送。
    /// OpenAI 沒有 <c>is_error</c>：錯誤結果就是內文為錯誤說明的一則 tool 訊息。
    /// <para>
    /// system prompt 是第一則 <c>system</c> 訊息；模式指示（<see cref="ChatRequest.TurnSuffix"/>）放在最尾端：
    /// 最後一則是使用者訊息時接在它的文字後面，否則（最後是 tool 訊息）另外加一則只有模式指示的 <c>user</c> 訊息。
    /// OpenAI 的前綴快取是自動的，只要這段前綴逐位元組相同就會命中，不需要標記（M5 的 Q2）。
    /// </para>
    /// </summary>
    internal static string BuildBody(ChatRequest request, IReadOnlyDictionary<string, byte[]>? images = null)
    {
        images ??= ImageAttachments.None;
        var messages = new List<object>();
        if (request.System is { Length: > 0 } system)
            messages.Add(new { role = "system", content = system });

        var suffix = request.TurnSuffix is { Length: > 0 } turnSuffix ? turnSuffix : null;
        var lastMessage = request.Messages.Count > 0 ? request.Messages[^1] : null;
        var suffixPlaced = false;
        var toolImageParts = new List<object>();   // 同一批 tool 訊息裡各結果的圖片，批次結束時合成一則 user 訊息（M18 D12）
        foreach (var message in request.Messages)
        {
            if (message.Role != ChatRole.Tool)
                FlushToolImages(messages, toolImageParts);

            switch (message.Role)
            {
                case ChatRole.User:
                {
                    // 使用者訊息：上下文區塊（檔案文字、檔案樹）與使用者打的字合成一段文字；圖片另外成為 image_url 部分
                    var text = ContextBlockFormatter.ToPromptText(message.Content.Where(b => b is TextBlock or ContextBlock));
                    var imageParts = new List<object>();
                    foreach (var image in message.Content.OfType<ImageBlock>())
                    {
                        if (images.TryGetValue(image.FileName, out var bytes))
                            imageParts.Add(new { type = "image_url", image_url = new { url = $"data:{image.MediaType};base64,{Convert.ToBase64String(bytes)}" } });
                        else
                            text = text.Length == 0 ? ImageAttachments.MissingText(image) : text + "\n\n" + ImageAttachments.MissingText(image);
                    }

                    if (suffix is not null && !suffixPlaced && ReferenceEquals(message, lastMessage))
                    {
                        text = text.Length == 0 ? suffix : text + "\n\n" + suffix;
                        suffixPlaced = true;
                    }

                    if (imageParts.Count > 0)
                    {
                        List<object> parts = [];
                        if (text.Length > 0)
                            parts.Add(new { type = "text", text });
                        parts.AddRange(imageParts);
                        messages.Add(new { role = "user", content = parts });
                    }
                    else if (text.Length > 0)
                    {
                        messages.Add(new { role = "user", content = text });
                    }
                    break;
                }
                case ChatRole.Assistant:
                {
                    var text = string.Concat(message.Content.OfType<TextBlock>().Select(b => b.Text));
                    var calls = message.Content.OfType<ToolUseBlock>()
                        .Select(u => new
                        {
                            id = u.Id,
                            type = "function",
                            function = new { name = u.Name, arguments = string.IsNullOrWhiteSpace(u.InputJson) ? "{}" : u.InputJson },
                        })
                        .ToList();
                    if (calls.Count > 0)
                        messages.Add(new { role = "assistant", content = text.Length > 0 ? text : null, tool_calls = calls });
                    else if (text.Length > 0)
                        messages.Add(new { role = "assistant", content = text });
                    break;
                }
                case ChatRole.Tool:
                    foreach (var result in message.Content.OfType<ToolResultBlock>())
                    {
                        messages.Add(new { role = "tool", tool_call_id = result.ToolUseId, content = result.Content });
                        // tool 角色不能帶圖片：圖片留到這一批 tool 訊息之後，讀不到的略過
                        foreach (var image in result.Images ?? [])
                        {
                            if (images.TryGetValue(image.FileName, out var bytes))
                                toolImageParts.Add(new { type = "image_url", image_url = new { url = $"data:{image.MediaType};base64,{Convert.ToBase64String(bytes)}" } });
                        }
                    }
                    break;
            }
        }
        FlushToolImages(messages, toolImageParts);

        if (suffix is not null && !suffixPlaced)
            messages.Add(new { role = "user", content = suffix });

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.Model.Model.ModelId,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
            ["max_tokens"] = request.EffectiveMaxOutputTokens,   // 小精靈模式有較低的上限（M5）
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.InputSchema },
            }).ToList();
        }

        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// 一批 tool 訊息之後，若有圖片，緊接著加一則 user 訊息（先一段說明文字，再每張圖一個 image_url）。
    /// 這則訊息是請求時從已存的區塊推導出來的，不存檔、不改歷史。
    /// </summary>
    private static void FlushToolImages(List<object> messages, List<object> imageParts)
    {
        if (imageParts.Count == 0)
            return;

        List<object> parts = [new { type = "text", text = "（以上工具產生的圖片）" }];
        parts.AddRange(imageParts);
        messages.Add(new { role = "user", content = parts });
        imageParts.Clear();
    }
}

internal static class OpenAIUrls
{
    public static Uri BaseUri(string baseUrl) => new(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    public static Uri Endpoint(Uri baseUri, string path) => new(baseUri, path);
}
