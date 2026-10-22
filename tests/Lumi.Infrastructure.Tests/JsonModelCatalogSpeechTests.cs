using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Speech;
using Lumi.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>語音設定（M6）在 settings.json 的讀寫、Token 的 Host 綁定。</summary>
public sealed class JsonModelCatalogSpeechTests : IDisposable
{
    private const string PlainToken = "speech-token-PLAIN-MARKER-4242";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-speech-tests-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    private JsonModelCatalog NewCatalog() =>
        new(Options.Create(new ModelCatalogOptions { SettingsPath = SettingsPath }), _secrets, NullLogger<JsonModelCatalog>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static SpeechSettings Speech(string endpoint = "https://example.test/speech/transcribe") =>
        new(endpoint, "zh", 120, null, null);

    private SecretValue? TokenValue => _secrets.Items.TryGetValue(SpeechSecrets.WhisperToken.Name, out var v) ? new SecretValue(v) : null;

    [Fact]
    public void Old_settings_file_without_speech_uses_the_default()
    {
        Directory.CreateDirectory(_directory);
        // 沒有 speech 欄位的舊檔（版本號仍是 1）
        File.WriteAllText(SettingsPath, """{"version":1,"connections":[],"models":[],"theme":"system"}""");

        var catalog = NewCatalog();

        Assert.Null(catalog.LoadProblem);
        Assert.Equal(SpeechSettings.Default, catalog.Current.Speech);
    }

    [Fact]
    public async Task Speech_settings_survive_reload_with_hint_and_host()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(new SpeechSettings("https://example.test/speech/transcribe", "en", 90, null, null), new SecretValue(PlainToken));

        var reloaded = NewCatalog().Current.Speech;

        Assert.Equal("https://example.test/speech/transcribe", reloaded.Endpoint);
        Assert.Equal("en", reloaded.Language);
        Assert.Equal(90, reloaded.TimeoutSeconds);
        Assert.Equal("4242", reloaded.TokenHint);
        Assert.Equal("example.test", reloaded.TokenHost);
    }

    [Fact]
    public async Task Token_is_never_written_to_the_settings_file()
    {
        var catalog = NewCatalog();

        await catalog.SetSpeechAsync(Speech(), new SecretValue(PlainToken));

        var text = File.ReadAllText(SettingsPath);
        Assert.DoesNotContain(PlainToken, text);
        Assert.Contains("4242", text);            // hint
        Assert.Contains("example.test", text);    // 綁定的主機
        Assert.Equal(PlainToken, _secrets.Items[SpeechSecrets.WhisperToken.Name]);
    }

    [Fact]
    public async Task Changing_the_host_without_a_new_token_removes_the_old_token()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(Speech("https://first.example.test/x"), new SecretValue(PlainToken));

        await catalog.SetSpeechAsync(Speech("https://second.example.test/x"), null);

        Assert.False(await catalog.HasSpeechTokenAsync());
        Assert.Empty(_secrets.Items);
        var saved = NewCatalog().Current.Speech;
        Assert.Null(saved.TokenHint);
        Assert.Null(saved.TokenHost);
        Assert.Equal("https://second.example.test/x", saved.Endpoint);
    }

    [Fact]
    public async Task Clearing_the_endpoint_also_drops_a_token_bound_to_a_host()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(Speech(), new SecretValue(PlainToken));

        await catalog.SetSpeechAsync(Speech(""), null);

        Assert.False(await catalog.HasSpeechTokenAsync());
        Assert.Null(catalog.Current.Speech.TokenHost);
    }

    [Fact]
    public async Task Same_host_without_a_new_token_keeps_the_token()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(Speech("https://example.test/a"), new SecretValue(PlainToken));

        // 路徑變了、Host 大小寫不同：Host 相同就視為同一個服務，Token 保留
        await catalog.SetSpeechAsync(Speech("https://EXAMPLE.test/b"), null);

        Assert.True(await catalog.HasSpeechTokenAsync());
        Assert.Equal("4242", catalog.Current.Speech.TokenHint);
        Assert.Equal("example.test", catalog.Current.Speech.TokenHost);
        Assert.Equal("https://EXAMPLE.test/b", catalog.Current.Speech.Endpoint);
    }

    [Fact]
    public async Task New_token_updates_hint_and_binds_to_the_new_host()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(Speech("https://first.example.test/x"), new SecretValue("old-token-1111"));

        await catalog.SetSpeechAsync(Speech("https://second.example.test/x"), new SecretValue("new-token-9999"));

        Assert.Equal("9999", catalog.Current.Speech.TokenHint);
        Assert.Equal("second.example.test", catalog.Current.Speech.TokenHost);
        Assert.Equal("new-token-9999", _secrets.Items[SpeechSecrets.WhisperToken.Name]);
    }

    [Fact]
    public async Task Same_host_with_a_new_token_updates_the_hint()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(Speech(), new SecretValue("old-token-1111"));

        await catalog.SetSpeechAsync(Speech(), new SecretValue("new-token-2222"));

        Assert.Equal("2222", catalog.Current.Speech.TokenHint);
        Assert.Equal("example.test", catalog.Current.Speech.TokenHost);
    }

    [Fact]
    public async Task Caller_supplied_hint_and_host_are_ignored()
    {
        var catalog = NewCatalog();

        await catalog.SetSpeechAsync(new SpeechSettings("https://example.test/x", "zh", 120, "FAKE", "evil.example.test"), null);

        Assert.Null(catalog.Current.Speech.TokenHint);
        Assert.Null(catalog.Current.Speech.TokenHost);
        Assert.False(await catalog.HasSpeechTokenAsync());
    }

    [Fact]
    public async Task Token_without_an_endpoint_is_rejected()
    {
        var catalog = NewCatalog();

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SetSpeechAsync(Speech(""), new SecretValue(PlainToken)));
        Assert.Empty(_secrets.Items);
    }

    [Theory]
    [InlineData("http://example.test/x")]
    [InlineData("https://user:pw@example.test/x")]
    [InlineData("https://example.test/x?a=1")]
    public async Task Invalid_endpoint_is_rejected(string endpoint)
    {
        var catalog = NewCatalog();

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.SetSpeechAsync(Speech(endpoint), null));
        Assert.Equal(SpeechSettings.Default, catalog.Current.Speech);
    }

    [Fact]
    public async Task Clear_token_removes_it_and_the_binding()
    {
        var catalog = NewCatalog();
        await catalog.SetSpeechAsync(Speech(), new SecretValue(PlainToken));

        await catalog.ClearSpeechTokenAsync();

        Assert.False(await catalog.HasSpeechTokenAsync());
        Assert.Null(catalog.Current.Speech.TokenHint);
        Assert.Null(catalog.Current.Speech.TokenHost);
        Assert.Equal("https://example.test/speech/transcribe", catalog.Current.Speech.Endpoint);
        Assert.Null(TokenValue);
    }
}
