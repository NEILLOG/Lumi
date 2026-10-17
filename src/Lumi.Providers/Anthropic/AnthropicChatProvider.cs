using System.Runtime.CompilerServices;
using global::Anthropic;
using global::Anthropic.Exceptions;
using BetaMessages = global::Anthropic.Models.Beta.Messages;
using Lumi.Core.Chat;
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
    private readonly IOptions<AnthropicDevOptions> _options;
    private readonly ILogger<AnthropicChatProvider> _logger;

    public AnthropicChatProvider(
        HttpClient httpClient,
        ISecretStore secrets,
        IOptions<AnthropicDevOptions> options,
        ILogger<AnthropicChatProvider> logger)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        SecretValue? apiKey = null;
        ChatFailure? secretFailure = null;
        try
        {
            apiKey = await _secrets.GetAsync(AnthropicDevOptions.ApiKeySecret, ct).ConfigureAwait(false);
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
            yield return new Failed(new ChatFailure(ChatFailureKind.MissingApiKey, "尚未設定 Anthropic API 金鑰。", Retryable: false));
            yield break;
        }

        var options = _options.Value;
        var client = new AnthropicClient { ApiKey = apiKey.Reveal(), BaseUrl = options.BaseUrl, HttpClient = _httpClient };
        var parameters = AnthropicRequestConverter.ToCreateParams(request, options);

        var accumulator = new AnthropicStreamAccumulator(request.ModelId);

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
