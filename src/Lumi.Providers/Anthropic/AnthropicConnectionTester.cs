using global::Anthropic;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.Providers.Anthropic;

/// <summary>列出模型：用官方 SDK 的 Models API。</summary>
public sealed class AnthropicConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly ISecretStore _secrets;

    public AnthropicConnectionTester(HttpClient httpClient, ISecretStore secrets)
    {
        _httpClient = httpClient;
        _secrets = secrets;
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectionSettings connection, SecretValue? apiKey, CancellationToken ct)
    {
        try
        {
            var key = apiKey ?? await _secrets.GetAsync(ConnectionSecrets.ApiKey(connection.Id), ct).ConfigureAwait(false);
            if (key is null)
                return ConnectionTestResult.Fail(new ChatFailure(ChatFailureKind.MissingApiKey, "尚未輸入 API 金鑰。", false));

            var client = new AnthropicClient { ApiKey = key.Reveal(), BaseUrl = connection.BaseUrl, HttpClient = _httpClient };
            var page = await client.Models.List(cancellationToken: ct).ConfigureAwait(false);
            var ids = page.Items.Select(m => m.ID).Where(id => id.Length > 0).Order(StringComparer.Ordinal).ToList();
            return ConnectionTestResult.Ok(ids);
        }
        catch (SecretStoreException)
        {
            return ConnectionTestResult.Fail(new ChatFailure(ChatFailureKind.SecretUnreadable, "金鑰無法讀取，請重新輸入。", false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ConnectionTestResult.Fail(AnthropicErrorMapper.ToChatFailure(ex));
        }
    }
}
