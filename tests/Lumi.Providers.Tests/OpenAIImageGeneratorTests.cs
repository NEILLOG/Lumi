using System.Net;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Images;
using Lumi.Core.Models;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Providers.Tests;

/// <summary>22F：OpenAI 相容的圖片生成與編輯（假的 HttpMessageHandler）。</summary>
public class OpenAIImageGeneratorTests
{
    private const string FakeApiKey = "sk-image-FAKE-KEY-1234";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 4, 5];

    private static readonly ConnectionSettings Gateway =
        new("gateway", "公司 Gateway", ProviderFormat.OpenAICompatible, "https://gateway.example/v1/");

    private static OpenAIImageGenerator Create(FakeImageHttpHandler handler, TestSupport.FakeSecretStore? secrets = null) =>
        new(new HttpClient(handler), secrets ?? TestSupport.FakeSecretStore.WithKey(FakeApiKey), NullLogger<OpenAIImageGenerator>.Instance);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes);

    private static string Data(string item) => $$"""{"data":[{{item}}]}""";

    private static ImageGenerationRequest GenerationRequest(string prompt = "一隻貓") =>
        new(Gateway, "gpt-image-2", prompt, "1536x1024");

    [Fact]
    public async Task Generate_SendsJsonWithBearerKey_AndReadsB64Json()
    {
        var handler = new FakeImageHttpHandler(_ => FakeImageHttpHandler.Json(Data($$"""{"b64_json":"{{B64(Png)}}","revised_prompt":"一隻橘貓"}""")));

        var image = await Create(handler).GenerateAsync(GenerationRequest());

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://gateway.example/v1/images/generations", sent.Url.AbsoluteUri);
        Assert.Equal($"Bearer {FakeApiKey}", sent.Authorization);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("gpt-image-2", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("一隻貓", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("1536x1024", body.RootElement.GetProperty("size").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("n").GetInt32());

        Assert.Equal(Png, image.Data);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("一隻橘貓", image.RevisedPrompt);
    }

    [Fact]
    public async Task Generate_WithUrlResponse_DownloadsTheImageWithoutTheKey()
    {
        var handler = new FakeImageHttpHandler(request => request.RequestUri!.Host == "gateway.example"
            ? FakeImageHttpHandler.Json("""{"data":[{"url":"https://cdn.example/out.jpg"}]}""")
            : FakeImageHttpHandler.Bytes(Jpeg));

        var image = await Create(handler).GenerateAsync(GenerationRequest());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://cdn.example/out.jpg", handler.Requests[1].Url.AbsoluteUri);
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Equal(Jpeg, image.Data);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Null(image.RevisedPrompt);
    }

    [Fact]
    public async Task Generate_WithoutAKey_SendsNoAuthorizationHeader()
    {
        var handler = new FakeImageHttpHandler(_ => FakeImageHttpHandler.Json(Data($$"""{"b64_json":"{{B64(Png)}}"}""")));

        await Create(handler, TestSupport.FakeSecretStore.Empty()).GenerateAsync(GenerationRequest());

        Assert.Null(Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task Edit_SendsMultipartWithFieldsAndOnePartPerImage()
    {
        var handler = new FakeImageHttpHandler(_ => FakeImageHttpHandler.Json(Data($$"""{"b64_json":"{{B64(Png)}}"}""")));
        var request = new ImageEditRequest(Gateway, "gpt-image-2", "make it night", "1024x1024",
            [new SourceImage(Png, "image/png", "a.png"), new SourceImage(Jpeg, "image/jpeg", "b.jpg")]);

        var image = await Create(handler).EditAsync(request);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://gateway.example/v1/images/edits", sent.Url.AbsoluteUri);
        Assert.StartsWith("multipart/form-data", sent.ContentType);
        Assert.Equal($"Bearer {FakeApiKey}", sent.Authorization);
        Assert.Contains("name=model", sent.Body);
        Assert.Contains("gpt-image-2", sent.Body);
        Assert.Contains("name=prompt", sent.Body);
        Assert.Contains("make it night", sent.Body);
        Assert.Contains("name=size", sent.Body);
        Assert.Contains("name=n", sent.Body);
        Assert.Equal(2, CountOccurrences(sent.Body, "image[]"));
        Assert.Contains("filename=a.png", sent.Body);
        Assert.Contains("filename=b.jpg", sent.Body);
        Assert.Contains("Content-Type: image/jpeg", sent.Body);
        Assert.Equal(Png, image.Data);
    }

    [Theory]
    [InlineData(401, ChatFailureKind.InvalidApiKey)]
    [InlineData(403, ChatFailureKind.PermissionDenied)]
    [InlineData(404, ChatFailureKind.ModelNotFound)]
    [InlineData(429, ChatFailureKind.RateLimited)]
    [InlineData(500, ChatFailureKind.ServerError)]
    public async Task HttpErrors_MapToTheChatFailureKind(int status, ChatFailureKind kind)
    {
        var handler = new FakeImageHttpHandler(_ => FakeImageHttpHandler.Json("{}", (HttpStatusCode)status));

        var error = await Assert.ThrowsAsync<ImageGenerationException>(() => Create(handler).GenerateAsync(GenerationRequest()));

        Assert.Equal(kind, error.Kind);
        Assert.DoesNotContain("{}", error.Message);
    }

    [Fact]
    public async Task NetworkFailure_MapsToNetwork()
    {
        var handler = new FakeImageHttpHandler(_ => throw new HttpRequestException("no route"));

        var error = await Assert.ThrowsAsync<ImageGenerationException>(() => Create(handler).GenerateAsync(GenerationRequest()));

        Assert.Equal(ChatFailureKind.Network, error.Kind);
    }

    [Fact]
    public async Task ResponseWithoutImage_IsAnError()
    {
        var handler = new FakeImageHttpHandler(_ => FakeImageHttpHandler.Json("""{"data":[]}"""));

        var error = await Assert.ThrowsAsync<ImageGenerationException>(() => Create(handler).GenerateAsync(GenerationRequest()));

        Assert.Equal(ChatFailureKind.Unknown, error.Kind);
    }

    [Fact]
    public async Task AnthropicConnection_IsRejectedWithoutSendingAnything()
    {
        var handler = new FakeImageHttpHandler(_ => FakeImageHttpHandler.Json("{}"));
        var anthropic = Gateway with { Format = ProviderFormat.Anthropic };

        var error = await Assert.ThrowsAsync<ImageGenerationException>(() =>
            Create(handler).GenerateAsync(new ImageGenerationRequest(anthropic, "m", "x", "1024x1024")));

        Assert.Equal(ChatFailureKind.BadRequest, error.Kind);
        Assert.Empty(handler.Requests);
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
