using System.Net;
using System.Security.Authentication;
using System.Text;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Speech.Tests.TestSupport;

namespace Lumi.Speech.Tests;

public class WhisperSpeechToTextTests
{
    private const string Endpoint = "https://example.test/speech/transcribe";
    private const string Token = "tok-PLAIN-MARKER-7777";
    private const string BodyMarker = "SERVER-BODY-MARKER";
    private const string OkJson = """{"text":" 你好 ","language":"zh","duration":1.5}""";

    private static readonly byte[] Wav = WavFormat.FromPcm(new byte[8]);

    private static SpeechSettings Settings(string language = "zh", int timeout = 120) =>
        new(Endpoint, language, timeout, "7777", "example.test");

    private static InMemorySecrets SecretsWithToken()
    {
        var secrets = new InMemorySecrets();
        secrets.Items[SpeechSecrets.WhisperToken.Name] = Token;
        return secrets;
    }

    private static WhisperSpeechToText Service(FakeWhisperHandler handler, SpeechSettings? settings = null, InMemorySecrets? secrets = null) =>
        new(new HttpClient(handler), new FakeSpeechCatalog(settings ?? Settings()), secrets ?? SecretsWithToken());

    private static Task<TranscriptionResult> Transcribe(WhisperSpeechToText service, string? sessionId = "conv-1", CancellationToken ct = default) =>
        service.TranscribeAsync(new MemoryStream(Wav), "voice.wav", sessionId, ct);

    private static void AssertSafeMessage(string message)
    {
        Assert.DoesNotContain(Token, message);
        Assert.DoesNotContain(BodyMarker, message);
        Assert.DoesNotContain("7777", message);   // 末 4 碼也不顯示在錯誤訊息裡
    }

    // ---- 表單與標頭 ----

    [Fact]
    public async Task Sends_the_documented_fields()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        await Transcribe(Service(handler), "conv-1");

        var request = handler.Last;
        Assert.Equal(Endpoint, request.Url.ToString());
        Assert.Contains("application/json", request.Accept);

        var form = request.Form;
        Assert.Equal("audio/wav", form.File.ContentType);
        Assert.Equal("voice.wav", form.File.FileName);
        Assert.Equal(Wav, Encoding.Latin1.GetBytes(form.File.Value));
        Assert.Equal("normal", form.Value("mode"));
        Assert.Equal("zh", form.Value("language"));
        Assert.Equal("json", form.Value("return_format"));
        Assert.Equal("Lumi", form.Value("source_pgm"));
        Assert.Equal("voice.wav", form.Value("file_name"));
        Assert.Equal("conv-1", form.Value("session_id"));
    }

    [Fact]
    public async Task Does_not_send_speaker_or_timeout_fields()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        await Transcribe(Service(handler));

        var form = handler.Last.Form;
        Assert.False(form.Has("min_speakers"));
        Assert.False(form.Has("max_speakers"));
        Assert.False(form.Has("timeout"));
    }

    [Theory]
    [InlineData("zh", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public async Task Language_is_sent_only_when_not_blank(string language, bool sent)
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        await Transcribe(Service(handler, Settings(language)));

        Assert.Equal(sent, handler.Last.Form.Has("language"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Session_id_is_sent_only_when_present(string? sessionId)
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        await Transcribe(Service(handler), sessionId);

        Assert.False(handler.Last.Form.Has("session_id"));
    }

    [Fact]
    public async Task Authorization_is_sent_when_the_token_host_matches()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        await Transcribe(Service(handler));

        Assert.Equal("Bearer", handler.Last.Authorization!.Scheme);
        Assert.Equal(Token, handler.Last.Authorization.Parameter);
    }

    [Fact]
    public async Task Token_bound_to_another_host_is_never_sent()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);
        var settings = new SpeechSettings(Endpoint, "zh", 120, "7777", "other.example.test");

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler, settings)));

        Assert.Equal(SpeechFailureKind.NotConfigured, failure.Kind);
        Assert.Equal(0, handler.CallCount);
        AssertSafeMessage(failure.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test/speech")]
    public async Task Missing_or_invalid_endpoint_is_not_configured(string endpoint)
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);
        var settings = new SpeechSettings(endpoint, "zh", 120, "7777", "example.test");

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler, settings)));

        Assert.Equal(SpeechFailureKind.NotConfigured, failure.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Missing_token_is_not_configured()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);
        var settings = new SpeechSettings(Endpoint, "zh", 120, null, null);

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler, settings)));

        Assert.Equal(SpeechFailureKind.NotConfigured, failure.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Token_missing_from_the_store_is_not_configured()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler, secrets: new InMemorySecrets())));

        Assert.Equal(SpeechFailureKind.NotConfigured, failure.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unreadable_token_is_not_configured_and_does_not_leak_the_store_message()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);
        var secrets = SecretsWithToken();
        secrets.ThrowOnGet = new SecretStoreException("鑰匙圈內部錯誤 " + Token);

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler, secrets: secrets)));

        Assert.Equal(SpeechFailureKind.NotConfigured, failure.Kind);
        Assert.Equal(0, handler.CallCount);
        AssertSafeMessage(failure.Message);
    }

    // ---- 結果 ----

    [Fact]
    public async Task Returns_the_parsed_result()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, OkJson);

        var result = await Transcribe(Service(handler));

        Assert.Equal("你好", result.Text);
        Assert.Equal("zh", result.Language);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.Duration);
    }

    [Fact]
    public async Task Unrecognized_response_is_reported()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, """{"foo":{"bar":"baz"}}""");

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler)));

        Assert.Equal(SpeechFailureKind.UnrecognizedResponse, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public async Task Blank_text_is_a_successful_empty_result()
    {
        var handler = FakeWhisperHandler.Json(HttpStatusCode.OK, """{"text":"  "}""");

        var result = await Transcribe(Service(handler));

        Assert.Equal("", result.Text);
    }

    // ---- 錯誤對應 ----

    [Theory]
    [InlineData(401, SpeechFailureKind.InvalidToken, false)]
    [InlineData(403, SpeechFailureKind.InvalidToken, false)]
    [InlineData(404, SpeechFailureKind.NotFound, false)]
    [InlineData(413, SpeechFailureKind.TooLarge, false)]
    [InlineData(429, SpeechFailureKind.RateLimited, true)]
    [InlineData(408, SpeechFailureKind.ServerTimeout, true)]
    [InlineData(504, SpeechFailureKind.ServerTimeout, true)]
    [InlineData(500, SpeechFailureKind.ServerError, true)]
    [InlineData(503, SpeechFailureKind.ServerError, true)]
    [InlineData(418, SpeechFailureKind.Unknown, false)]
    public async Task HTTP_status_is_classified_without_the_response_body(int status, SpeechFailureKind kind, bool retryable)
    {
        // 回應內文故意放入標記與 Token：錯誤訊息都不能帶出來
        var handler = FakeWhisperHandler.Reply((HttpStatusCode)status, "text/plain", $"{BodyMarker} echo {Token}");

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler)));

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(retryable, failure.Retryable);
        AssertSafeMessage(failure.Message);
    }

    [Fact]
    public async Task Connection_failure_is_network()
    {
        var handler = FakeWhisperHandler.Throws(new HttpRequestException("連線被拒絕"));

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler)));

        Assert.Equal(SpeechFailureKind.Network, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Contains("VPN", failure.Message);
    }

    [Fact]
    public async Task IO_failure_is_network()
    {
        var handler = FakeWhisperHandler.Throws(new IOException("連線中斷"));

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler)));

        Assert.Equal(SpeechFailureKind.Network, failure.Kind);
    }

    [Fact]
    public async Task Untrusted_certificate_is_explained_and_never_bypassed()
    {
        var handler = FakeWhisperHandler.Throws(new HttpRequestException("憑證", new AuthenticationException("untrusted")));

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Transcribe(Service(handler)));

        Assert.Equal(SpeechFailureKind.CertificateUntrusted, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("受信任的根憑證授權單位", failure.Message);
        Assert.Contains("鑰匙圈存取", failure.Message);
        Assert.Contains("永遠信任", failure.Message);
        Assert.DoesNotContain("略過", failure.Message);
    }

    // ---- 逾時與取消 ----

    [Fact]
    public async Task Local_timeout_is_reported_with_the_configured_seconds()
    {
        var handler = FakeWhisperHandler.Hangs();

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() =>
            Transcribe(Service(handler, Settings(timeout: SpeechSettings.MinTimeoutSeconds))));

        Assert.Equal(SpeechFailureKind.LocalTimeout, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Contains("5 秒", failure.Message);
        Assert.Contains("逾時秒數", failure.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var handler = FakeWhisperHandler.Hangs();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Transcribe(Service(handler), ct: cts.Token));

        Assert.IsNotType<SpeechFailure>(ex);
    }
}
