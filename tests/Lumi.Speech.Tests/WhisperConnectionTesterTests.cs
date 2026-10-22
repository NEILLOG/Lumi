using System.Net;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Speech.Tests.TestSupport;

namespace Lumi.Speech.Tests;

public class WhisperConnectionTesterTests
{
    private const string Endpoint = "https://example.test/speech/transcribe";
    private const string Token = "tok-PLAIN-MARKER-5555";

    private static readonly SpeechSettings Settings = new(Endpoint, "zh", 120, "5555", "example.test");

    private static WhisperConnectionTester Tester(FakeWhisperHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task Success_sends_half_a_second_of_silence_with_the_given_token()
    {
        // 回應內容解析不出文字也算成功：這裡只要 2xx
        var handler = FakeWhisperHandler.Reply(HttpStatusCode.OK, "text/plain", "ok");

        await Tester(handler).TestAsync(Settings, new SecretValue(Token), default);

        var request = handler.Last;
        Assert.Equal(Endpoint, request.Url.ToString());
        Assert.Equal(Token, request.Authorization!.Parameter);

        var form = request.Form;
        Assert.Equal("audio/wav", form.File.ContentType);
        Assert.Equal("Lumi", form.Value("source_pgm"));
        Assert.Equal("zh", form.Value("language"));
        Assert.False(form.Has("session_id"));
        // 0.5 秒 × 16000 取樣 × 2 位元組 = 16000 位元組 PCM，外加 44 位元組檔頭
        Assert.Equal(WavFormat.HeaderSize + 16_000, form.File.Value.Length);
        Assert.Equal("RIFF", form.File.Value[..4]);
        Assert.All(form.File.Value[WavFormat.HeaderSize..], c => Assert.Equal('\0', c));
    }

    [Fact]
    public async Task Uses_the_unsaved_settings_not_the_stored_ones()
    {
        var handler = FakeWhisperHandler.Reply(HttpStatusCode.OK, "text/plain", "ok");
        var unsaved = new SpeechSettings("https://example.test/other", "en", 120, null, null);

        await Tester(handler).TestAsync(unsaved, new SecretValue(Token), default);

        Assert.Equal("https://example.test/other", handler.Last.Url.ToString());
        Assert.Equal("en", handler.Last.Form.Value("language"));
    }

    [Fact]
    public async Task Invalid_endpoint_fails_before_anything_is_sent()
    {
        var handler = FakeWhisperHandler.Reply(HttpStatusCode.OK, "text/plain", "ok");
        var settings = new SpeechSettings("http://example.test/x", "zh", 120, null, null);

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Tester(handler).TestAsync(settings, new SecretValue(Token), default));

        Assert.Equal(SpeechFailureKind.NotConfigured, failure.Kind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Rejected_token_is_reported_without_the_token()
    {
        var handler = FakeWhisperHandler.Reply(HttpStatusCode.Unauthorized, "text/plain", $"bad token {Token}");

        var failure = await Assert.ThrowsAsync<SpeechFailure>(() => Tester(handler).TestAsync(Settings, new SecretValue(Token), default));

        Assert.Equal(SpeechFailureKind.InvalidToken, failure.Kind);
        Assert.DoesNotContain(Token, failure.Message);
    }
}
