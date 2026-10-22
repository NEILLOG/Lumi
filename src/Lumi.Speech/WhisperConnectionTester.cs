using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.Speech;

/// <summary>
/// 測試連線：送 0.5 秒靜音一次。只要服務回 2xx 就算成功，**不檢查辨識內容**（解析不到文字也算成功）。
/// 用傳入的（尚未儲存的）設定與 Token，不讀 catalog。
/// </summary>
public sealed class WhisperConnectionTester : ISpeechConnectionTester
{
    private static readonly TimeSpan ProbeLength = TimeSpan.FromMilliseconds(500);

    private readonly HttpClient _httpClient;

    public WhisperConnectionTester(HttpClient httpClient) => _httpClient = httpClient;

    public async Task TestAsync(SpeechSettings settings, SecretValue token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(token);

        var normalized = settings.Normalize();
        var endpoint = WhisperTransport.RequireEndpoint(normalized);

        using var silence = new MemoryStream(WavFormat.Silence(ProbeLength));
        await WhisperTransport.PostAsync(_httpClient, endpoint, token, normalized, silence, "connection-test.wav", null, ct)
            .ConfigureAwait(false);
    }
}
