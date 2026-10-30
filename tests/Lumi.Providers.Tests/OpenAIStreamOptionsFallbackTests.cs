using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;

namespace Lumi.Providers.Tests;

/// <summary>
/// 不認得 <c>stream_options</c> 的舊版相容服務（Azure 舊 api-version）：400 且內文提到它時，拿掉欄位重送一次，並記住這個連線。
/// </summary>
public class OpenAIStreamOptionsFallbackTests
{
    private const string UnknownStreamOptions = """{"error":{"message":"Unknown parameter: 'stream_options'.","type":"invalid_request_error"}}""";

    private static readonly ConnectionSettings Connection =
        new("test-azure", "測試 Azure", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ResolvedModel Model() =>
        new(new ModelSettings("m1", Connection.Id, "gpt-test", "GPT Test", 2048, 128_000, true), Connection);

    private static ChatRequest SimpleRequest() => new(Model(),
        [new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new TextBlock("Hi")], DateTimeOffset.UtcNow)]);

    private static OpenAICompatibleChatProvider Create(ScriptedHandler handler) =>
        new(new HttpClient(handler), FakeSecretStore.WithKey("sk-test"), new CapturingLogger<OpenAICompatibleChatProvider>());

    private static async Task<List<ChatEvent>> Collect(OpenAICompatibleChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request, CancellationToken.None))
            events.Add(e);
        return events;
    }

    private static string AllText(IEnumerable<ChatEvent> events) => string.Concat(events.OfType<TextDelta>().Select(e => e.Text));

    /// <summary>回傳 HTTP 錯誤的腳本步驟（每次請求用一個）。</summary>
    private static Func<HttpResponseMessage> Status(HttpStatusCode status, string body) => () => new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>回傳一份正常的 SSE 腳本步驟（basic-text：Hello, world!）。</summary>
    private static Func<HttpResponseMessage> Sse() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ChunkedMemoryStream(Fixtures.OpenAISse("basic-text"), 64))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
        },
    };

    [Fact]
    public async Task FallbackWithoutStreamOptions_On400MentioningIt()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest, UnknownStreamOptions), Sse());
        var provider = Create(handler);

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal("Hello, world!", AllText(events));
        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("stream_options", handler.Bodies[0]);
        Assert.DoesNotContain("stream_options", handler.Bodies[1]);
    }

    [Fact]
    public async Task RemembersConnection()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest, UnknownStreamOptions), Sse(), Sse());
        var provider = Create(handler);
        await Collect(provider, SimpleRequest());

        var events = await Collect(provider, SimpleRequest());

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.DoesNotContain("stream_options", handler.Bodies[2]);
    }

    [Fact]
    public async Task Other400_NoRetry()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest, """{"error":{"message":"messages must not be empty"}}"""), Sse());
        var provider = Create(handler);

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal(ChatFailureKind.BadRequest, Assert.IsType<Failed>(Assert.Single(events)).Failure.Kind);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task RetryOnlyOnce_SecondRejectionIsANormalError()
    {
        var handler = new ScriptedHandler(
            Status(HttpStatusCode.BadRequest, UnknownStreamOptions),
            Status(HttpStatusCode.BadRequest, UnknownStreamOptions),
            Sse());
        var provider = Create(handler);

        var events = await Collect(provider, SimpleRequest());

        Assert.Equal(ChatFailureKind.BadRequest, Assert.IsType<Failed>(Assert.Single(events)).Failure.Kind);
        Assert.Equal(2, handler.Bodies.Count);
    }

    /// <summary>依序回傳腳本步驟，並記下每次請求的內文。腳本用完後再請求會丟例外。</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;

        public ScriptedHandler(params Func<HttpResponseMessage>[] steps) => _steps = new(steps);

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return _steps.Dequeue()();
        }
    }
}
