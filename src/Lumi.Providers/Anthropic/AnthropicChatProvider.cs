using System.Runtime.CompilerServices;
using global::Anthropic;
using global::Anthropic.Exceptions;
using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Anthropic;

/// <summary>
/// Anthropic 原生格式的 <see cref="IChatProvider"/>。走官方 SDK 的 beta 端點（<c>client.Beta.Messages</c>），
/// 因為 server-side fallback（Q9）只在 beta 有。金鑰每次呼叫才從 <see cref="ISecretStore"/> 讀，不快取、
/// 不放進建構子，避免明文長時間留在記憶體。
/// </summary>
public sealed class AnthropicChatProvider : IChatProvider
{
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;
    private readonly IOptions<AnthropicProviderOptions> _options;
    private readonly ILogger<AnthropicChatProvider> _logger;
    private readonly IAttachmentStore _attachments;

    public AnthropicChatProvider(
        HttpClient httpClient,
        ISecretStore secrets,
        IOptions<AnthropicProviderOptions> options,
        ILogger<AnthropicChatProvider> logger,
        IAttachmentStore? attachments = null)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _options = options;
        _logger = logger;
        _attachments = attachments ?? new NullAttachmentStore();
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        SecretValue? apiKey = null;
        ChatFailure? secretFailure = null;
        try
        {
            apiKey = await _secrets.GetAsync(ConnectionSecrets.ApiKey(request.Model.Connection.Id), ct).ConfigureAwait(false);
        }
        catch (SecretStoreException ex)
        {
            _logger.LogWarning(ex, "讀取 Anthropic 金鑰失敗");
            secretFailure = new ChatFailure(ChatFailureKind.SecretUnreadable, "金鑰無法讀取，請重新輸入。", Retryable: false);
        }

        if (secretFailure is not null)
        {
            yield return new Failed(secretFailure);
            yield break;
        }

        if (apiKey is null)
        {
            yield return new Failed(new ChatFailure(ChatFailureKind.MissingApiKey, $"連線「{request.Model.Connection.Name}」尚未設定 API 金鑰。", Retryable: false));
            yield break;
        }

        var options = _options.Value;
        var client = new AnthropicClient { ApiKey = apiKey.Reveal(), BaseUrl = request.Model.Connection.BaseUrl, HttpClient = _httpClient };
        var images = await ImageAttachments.LoadAsync(_attachments, request.Messages, ct).ConfigureAwait(false);
        var parameters = AnthropicRequestConverter.ToCreateParams(request, options, images);

        var accumulator = new AnthropicStreamAccumulator(request.Model.Model.ModelId);

        // enumerator 故意延後到迴圈第一輪、在 try 裡才建立：CreateStreaming(...).GetAsyncEnumerator(...)
        // 本身有沒有立刻送出 HTTP 請求（因而可能立刻丟出例外）是 SDK 的實作細節，不能假設一定是延遲求值。
        // 建到迴圈外面會讓「請求一開始就失敗」繞過下面的 try/catch，直接讓例外往外丟。
        IAsyncEnumerator<BetaMessages.BetaRawMessageStreamEvent>? enumerator = null;
        try
        {
            while (true)
            {
                List<ChatEvent>? produced = null;
                ChatFailure? failure = null;
                var done = false;

                try
                {
                    enumerator ??= client.Beta.Messages.CreateStreaming(parameters, cancellationToken: ct)
                        .GetAsyncEnumerator(ct);

                    if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        produced = accumulator.Process(enumerator.Current);
                    }
                    else
                    {
                        done = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 畫面上只有一句分類過的說明；伺服器回的錯誤內容（例如 400 是哪個欄位不對）只留在記錄檔
                    _logger.LogWarning(ex, "Anthropic 請求失敗（模型 {Model}）：{ResponseBody}",
                        request.Model.Model.ModelId, (ex as AnthropicApiException)?.ResponseBody);
                    failure = AnthropicErrorMapper.ToChatFailure(ex);
                    done = true;
                }

                if (produced is not null)
                {
                    foreach (var chatEvent in produced)
                        yield return chatEvent;
                }

                if (done)
                {
                    if (failure is not null)
                        yield return new Failed(failure);
                    yield break;
                }
            }
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }
}
