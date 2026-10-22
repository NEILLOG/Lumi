using System.Net.Http.Headers;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.Speech;

/// <summary>轉寫與測試連線共用的請求組裝與送出（只有 internal，不對外）。</summary>
internal static class WhisperTransport
{
    /// <summary>來源程式名稱，固定送出（agent.md 第 11 節）。</summary>
    public const string SourceProgram = "Lumi";

    /// <summary>端點必須通過 <see cref="SpeechEndpointRules"/>；沒設定或不合法都丟 <see cref="SpeechFailureKind.NotConfigured"/>。</summary>
    public static Uri RequireEndpoint(SpeechSettings settings)
    {
        var check = SpeechEndpointRules.Validate(settings.Endpoint);
        if (check.IsValid)
            return check.Uri!;

        var message = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? "尚未設定語音服務端點，請到設定填入。"
            : $"語音服務端點設定有誤：{check.Reason}";
        throw new SpeechFailure(SpeechFailureKind.NotConfigured, message, false);
    }

    /// <summary>
    /// 讀出 Token，但只在 Token 綁定的主機與端點相同時。換了主機就不送，要求重新輸入（主計畫的 Host 綁定規則）。
    /// </summary>
    public static async Task<SecretValue> LoadBoundTokenAsync(ISecretStore secrets, SpeechSettings settings, Uri endpoint, CancellationToken ct)
    {
        if (settings.TokenHost is null)
            throw new SpeechFailure(SpeechFailureKind.NotConfigured, "尚未設定語音 Token，請到設定輸入。", false);
        if (!string.Equals(settings.TokenHost, endpoint.Host, StringComparison.OrdinalIgnoreCase))
            throw new SpeechFailure(SpeechFailureKind.NotConfigured, "端點主機已變更，請重新輸入 Token。", false);

        SecretValue? token;
        try
        {
            token = await secrets.GetAsync(SpeechSecrets.WhisperToken, ct).ConfigureAwait(false);
        }
        catch (SecretStoreException)
        {
            throw new SpeechFailure(SpeechFailureKind.NotConfigured, "語音 Token 無法讀取，請重新輸入。", false);
        }

        return token ?? throw new SpeechFailure(SpeechFailureKind.NotConfigured, "尚未設定語音 Token，請到設定輸入。", false);
    }

    /// <summary>
    /// 送出一次 multipart 請求並回傳成功（2xx）的內文。非 2xx 與連線錯誤轉成 <see cref="SpeechFailure"/>；
    /// 本機逾時與呼叫端取消分開處理（取消時 <see cref="OperationCanceledException"/> 原樣往外丟）。
    /// </summary>
    public static async Task<WhisperReply> PostAsync(
        HttpClient httpClient, Uri endpoint, SecretValue token, SpeechSettings settings,
        Stream wav, string fileName, string? sessionId, CancellationToken ct)
    {
        using var form = BuildForm(settings, wav, fileName, sessionId);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // 認證標頭只設在這一次請求上（同 AddLumiProviders 的做法），Reveal 只在這裡呼叫
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Reveal());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw WhisperErrorMapper.FromResponse(response);

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new WhisperReply(response.Content.Headers.ContentType?.MediaType, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new SpeechFailure(SpeechFailureKind.LocalTimeout,
                $"超過 {settings.TimeoutSeconds} 秒沒有回應，可調高逾時秒數。", true);
        }
        catch (HttpRequestException ex)
        {
            throw WhisperErrorMapper.FromException(ex);
        }
        catch (IOException ex)
        {
            throw WhisperErrorMapper.FromException(ex);
        }
    }

    /// <summary>欄位依舊版實際送出的內容：<c>mode</c> 固定 normal，<c>language</c>、<c>session_id</c> 只在有值時才送。</summary>
    private static MultipartFormDataContent BuildForm(SpeechSettings settings, Stream wav, string fileName, string? sessionId)
    {
        var form = new MultipartFormDataContent();

        var file = new StreamContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", fileName);

        form.Add(new StringContent("normal"), "mode");
        if (!string.IsNullOrWhiteSpace(settings.Language))
            form.Add(new StringContent(settings.Language), "language");
        form.Add(new StringContent("json"), "return_format");
        form.Add(new StringContent(SourceProgram), "source_pgm");
        form.Add(new StringContent(fileName), "file_name");
        if (!string.IsNullOrWhiteSpace(sessionId))
            form.Add(new StringContent(sessionId), "session_id");

        return form;
    }
}

/// <summary>成功的回應：media type（不含參數）與內文。</summary>
internal sealed record WhisperReply(string? MediaType, string Body);
