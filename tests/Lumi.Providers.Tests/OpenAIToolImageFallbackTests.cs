using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;

namespace Lumi.Providers.Tests;

/// <summary>
/// 沒有視覺能力的模型拒絕工具結果的圖片（400／422）時，拿掉圖片重送一次：圖片留在歷史裡，不退回的話這則對話之後每個請求都會失敗。
/// </summary>
public class OpenAIToolImageFallbackTests
{
    private static readonly ConnectionSettings Connection =
        new("test-local", "測試地端", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ResolvedModel Model() =>
        new(new ModelSettings("m1", Connection.Id, "local-test", "Local", 2048, 128_000, true), Connection);

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] blocks) => new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    private static ChatRequest ToolImageRequest() => new(Model(),
    [
        Msg(ChatRole.User, new TextBlock("看一下畫面")),
        Msg(ChatRole.Assistant, new ToolUseBlock("t1", "mcp__s__shot", "{}")),
        Msg(ChatRole.Tool, new ToolResultBlock("t1", "視窗狀態", false, "截圖", null, null, 1, ToolApproval.NotRequired, [new ImageBlock("a.png", "image/png", "MCP 圖片 1.png")])),
    ]);

    private static OpenAICompatibleChatProvider Create(ScriptedHandler handler)
    {
        var store = new FakeAttachmentStore();
        store.Files["a.png"] = Png;
        return new(new HttpClient(handler), FakeSecretStore.WithKey("sk-test"), new CapturingLogger<OpenAICompatibleChatProvider>(), store);
    }

    private static async Task<List<ChatEvent>> Collect(OpenAICompatibleChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request, CancellationToken.None))
            events.Add(e);
        return events;
    }

    private static Func<HttpResponseMessage> Status(HttpStatusCode status, string body = """{"error":{"message":"image input is not supported"}}""") => () => new HttpResponseMessage(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static Func<HttpResponseMessage> Sse() => () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ChunkedMemoryStream(Fixtures.OpenAISse("basic-text"), 64))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("text/event-stream") },
        },
    };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Rejected_tool_images_are_resent_as_text(HttpStatusCode status)
    {
        var handler = new ScriptedHandler(Status(status), Sse());

        var events = await Collect(Create(handler), ToolImageRequest());

        Assert.IsType<Completed>(events[^1]);
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("image_url", handler.Bodies[0]);
        Assert.DoesNotContain("image_url", handler.Bodies[1]);
        var toolMessage = JsonNode.Parse(handler.Bodies[1])!["messages"]!.AsArray().Single(m => (string?)m!["role"] == "tool")!;
        Assert.Equal($"視窗狀態\n{OpenAICompatibleChatProvider.ToolImagesOmittedNote}", (string?)toolMessage["content"]);
    }

    [Fact]
    public async Task The_next_request_tries_images_again()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest), Sse(), Sse());
        var provider = Create(handler);
        await Collect(provider, ToolImageRequest());

        await Collect(provider, ToolImageRequest());

        Assert.Contains("image_url", handler.Bodies[2]);
    }

    [Fact]
    public async Task Resends_only_once_then_reports_the_failure()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest), Status(HttpStatusCode.BadRequest));

        var events = await Collect(Create(handler), ToolImageRequest());

        Assert.Equal(ChatFailureKind.BadRequest, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        Assert.Equal(2, handler.Bodies.Count);
    }

    [Fact]
    public async Task Context_length_errors_are_not_resent()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest, """{"error":{"code":"context_length_exceeded"}}"""));

        var events = await Collect(Create(handler), ToolImageRequest());

        Assert.Equal(ChatFailureKind.ContextTooLong, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Images_the_user_attached_are_not_dropped_silently()
    {
        var handler = new ScriptedHandler(Status(HttpStatusCode.BadRequest));
        var request = new ChatRequest(Model(), [Msg(ChatRole.User, new TextBlock("這是什麼"), new ImageBlock("a.png", "image/png", "a.png"))]);

        var events = await Collect(Create(handler), request);

        Assert.Equal(ChatFailureKind.BadRequest, Assert.IsType<Failed>(events[^1]).Failure.Kind);
        Assert.Single(handler.Bodies);
    }

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
