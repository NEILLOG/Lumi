using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.Speech;

/// <summary>
/// 公司內部 Whisper 服務的語音轉文字（端點由使用者在設定頁填入）。端點與 Token 每次從設定讀取，
/// 所以設定頁改了就立刻生效。
/// </summary>
public sealed class WhisperSpeechToText : ISpeechToText
{
    private readonly HttpClient _httpClient;
    private readonly IModelCatalog _catalog;
    private readonly ISecretStore _secrets;

    public WhisperSpeechToText(HttpClient httpClient, IModelCatalog catalog, ISecretStore secrets)
    {
        _httpClient = httpClient;
        _catalog = catalog;
        _secrets = secrets;
    }

    public async Task<TranscriptionResult> TranscribeAsync(Stream wav, string fileName, string? sessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wav);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var settings = _catalog.Current.Speech.Normalize();
        var endpoint = WhisperTransport.RequireEndpoint(settings);
        var token = await WhisperTransport.LoadBoundTokenAsync(_secrets, settings, endpoint, ct).ConfigureAwait(false);

        var reply = await WhisperTransport.PostAsync(_httpClient, endpoint, token, settings, wav, fileName, sessionId, ct)
            .ConfigureAwait(false);

        return WhisperResponseParser.Parse(reply.MediaType, reply.Body, settings.Language)
            ?? throw new SpeechFailure(SpeechFailureKind.UnrecognizedResponse, "語音服務回應的格式無法辨識。", false);
    }
}
