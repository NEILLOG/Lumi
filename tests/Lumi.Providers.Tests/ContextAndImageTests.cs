using System.Net;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

/// <summary>4B：使用者訊息裡的上下文區塊（檔案文字、檔案樹）與圖片，在兩種格式裡的樣子。</summary>
public class ContextAndImageTests
{
    private static readonly ConnectionSettings AnthropicConnection = new("a", "A", ProviderFormat.Anthropic, "https://api.anthropic.test");
    private static readonly ConnectionSettings OpenAiConnection = new("o", "O", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ResolvedModel AnthropicModel => new(new ModelSettings("m1", "a", "claude-opus-5-5", "C", 1024, 1_000_000, true), AnthropicConnection);
    private static ResolvedModel OpenAiModel => new(new ModelSettings("m2", "o", "gpt-test", "G", 1024, 128_000, true), OpenAiConnection);

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 8, 7];

    private static AnthropicChatProvider Anthropic(FakeAnthropicHttpHandler handler, FakeAttachmentStore store) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") }, FakeSecretStore.WithKey("sk-ant-test-FAKE-KEY-0000"),
        Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }), new CapturingLogger<AnthropicChatProvider>(), store);

    private static OpenAICompatibleChatProvider OpenAi(FakeAnthropicHttpHandler handler, FakeAttachmentStore store) =>
        new(new HttpClient(handler), FakeSecretStore.WithKey("sk-openai-FAKE-KEY-1234"), new CapturingLogger<OpenAICompatibleChatProvider>(), store);

    private static ChatMessage User(params ContentBlock[] blocks) => new(ChatMessage.NewId(), ChatRole.User, blocks, DateTimeOffset.UtcNow);

    private static async Task<List<ChatEvent>> Collect(IChatProvider provider, ChatRequest request)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamAsync(request))
            events.Add(e);
        return events;
    }

    private static ContextBlock File(string text = "第一行\n第二行") => new(ContextKind.File, "a.txt", "/proj/a.txt", text, true);

    // ================= Anthropic =================

    [Fact]
    public async Task Anthropic_FileContext_BecomesATextBlock_BeforeTheUsersText()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        await Collect(Anthropic(handler, new FakeAttachmentStore()), new ChatRequest(AnthropicModel, [User(File(), new TextBlock("看這個"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(["text", "text"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("<file path=\"/proj/a.txt\">\n     1\t第一行\n     2\t第二行\n</file>", content[0].GetProperty("text").GetString());
        Assert.Equal("看這個", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Anthropic_FolderTreeAndNotEmbeddedNote_AreText()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var folder = new ContextBlock(ContextKind.Folder, "proj", "/proj", "proj/\n  a.cs\n", true, null, 1);
        var big = new ContextBlock(ContextKind.File, "big.log", "/proj/big.log", null, false, "太大，請用 read_file 讀取");

        await Collect(Anthropic(handler, new FakeAttachmentStore()), new ChatRequest(AnthropicModel, [User(folder, big, new TextBlock("問"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("<folder path=\"/proj\" entries=\"1\">\nproj/\n  a.cs\n</folder>", content[0].GetProperty("text").GetString());
        Assert.Equal("<file path=\"/proj/big.log\" note=\"太大，請用 read_file 讀取\" />", content[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("image/gif", "image/gif")]
    [InlineData("image/webp", "image/webp")]
    public async Task Anthropic_Image_IsSentAsBase64WithItsMediaType(string mediaType, string expected)
    {
        var store = new FakeAttachmentStore();
        store.Files["pic.bin"] = Png;
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        await Collect(Anthropic(handler, store), new ChatRequest(AnthropicModel, [User(new ImageBlock("pic.bin", mediaType, "截圖.png"), new TextBlock("這是什麼"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var image = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("image", image.GetProperty("type").GetString());
        var source = image.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal(expected, source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(Png), source.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_MissingImageFile_BecomesAPlaceholder_AndTheRequestStillWorks()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);

        var events = await Collect(Anthropic(handler, new FakeAttachmentStore()),
            new ChatRequest(AnthropicModel, [User(new ImageBlock("gone.png", "image/png", "舊截圖.png"), new TextBlock("看"))]));

        Assert.IsType<Completed>(events[^1]);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var first = doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        Assert.Contains("舊截圖.png", first.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Anthropic_ImagesInEarlierMessages_AreResentEveryTime()
    {
        var store = new FakeAttachmentStore();
        store.Files["pic.png"] = Png;
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var history = new[]
        {
            User(new ImageBlock("pic.png", "image/png", "p"), new TextBlock("一")),
            new ChatMessage("a", ChatRole.Assistant, [new TextBlock("看到了")], DateTimeOffset.UtcNow),
            User(new TextBlock("二")),
        };

        await Collect(Anthropic(handler, store), new ChatRequest(AnthropicModel, history));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("image", doc.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
    }

    // ================= OpenAI 相容 =================

    [Fact]
    public async Task OpenAi_FileContext_IsMergedIntoTheUsersText()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(OpenAi(handler, new FakeAttachmentStore()), new ChatRequest(OpenAiModel, [User(File(), new TextBlock("看這個"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Equal("<file path=\"/proj/a.txt\">\n     1\t第一行\n     2\t第二行\n</file>\n\n看這個", content.GetString());
    }

    [Fact]
    public async Task OpenAi_Image_BecomesAnImageUrlPartWithADataUri()
    {
        var store = new FakeAttachmentStore();
        store.Files["pic.png"] = Png;
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(OpenAi(handler, store), new ChatRequest(OpenAiModel, [User(File(), new ImageBlock("pic.png", "image/png", "p"), new TextBlock("這是什麼"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(["text", "image_url"], content.EnumerateArray().Select(p => p.GetProperty("type").GetString()));
        Assert.Contains("這是什麼", content[0].GetProperty("text").GetString());
        Assert.Contains("<file path=", content[0].GetProperty("text").GetString());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(Png)}", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public async Task OpenAi_MissingImageFile_BecomesAPlaceholderInTheText()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(OpenAi(handler, new FakeAttachmentStore()),
            new ChatRequest(OpenAiModel, [User(new ImageBlock("gone.png", "image/png", "舊截圖.png"), new TextBlock("看"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var content = doc.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Contains("舊截圖.png", content.GetString());
    }

    [Fact]
    public async Task OpenAi_MessagesWithoutImages_KeepTheStringContentFormat()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(OpenAi(handler, new FakeAttachmentStore()), new ChatRequest(OpenAiModel, [User(new TextBlock("嗨"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("嗨", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task OpenAi_MultipleTextBlocks_AreSeparatedByABlankLine()
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        await Collect(OpenAi(handler, new FakeAttachmentStore()), new ChatRequest(OpenAiModel, [User(new TextBlock("甲"), new TextBlock("乙"))]));

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("甲\n\n乙", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    // ---- 400 的提示 ----

    private static async Task<string> BadRequestMessage(bool withImage, bool withTools)
    {
        var store = new FakeAttachmentStore();
        store.Files["pic.png"] = Png;
        var handler = FakeAnthropicHttpHandler.ForError(HttpStatusCode.BadRequest, Fixtures.OpenAIHttpError("400"));
        var blocks = withImage ? new ContentBlock[] { new ImageBlock("pic.png", "image/png", "p"), new TextBlock("看") } : [new TextBlock("看")];
        var request = new ChatRequest(OpenAiModel, [User(blocks)], withTools ? [new EchoTool()] : null);

        var events = await Collect(OpenAi(handler, store), request);

        return Assert.IsType<Failed>(events[^1]).Failure.Message;
    }

    [Fact]
    public async Task BadRequest_WithImages_HintsThatTheModelMayNotSupportImages()
    {
        var message = await BadRequestMessage(withImage: true, withTools: false);

        Assert.Contains("不支援圖片", message);
        Assert.DoesNotContain("工具", message);
    }

    [Fact]
    public async Task BadRequest_WithImagesAndTools_MentionsBoth()
    {
        var message = await BadRequestMessage(withImage: true, withTools: true);

        Assert.Contains("工具或圖片", message);
    }

    [Fact]
    public async Task BadRequest_WithNeither_HasNoHint()
    {
        var message = await BadRequestMessage(withImage: false, withTools: false);

        Assert.DoesNotContain("不支援", message);
    }

    // ================= 跨格式 =================

    [Fact]
    public async Task SameHistory_WorksOnBothFormats()
    {
        var store = new FakeAttachmentStore();
        store.Files["pic.png"] = Png;
        var history = new[] { User(File(), new ImageBlock("pic.png", "image/png", "p"), new TextBlock("看")) };
        var anthropicHandler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        var openAiHandler = FakeAnthropicHttpHandler.ForSse(Fixtures.OpenAISse("basic-text"), 64);

        var a = await Collect(Anthropic(anthropicHandler, store), new ChatRequest(AnthropicModel, history));
        var o = await Collect(OpenAi(openAiHandler, store), new ChatRequest(OpenAiModel, history));

        Assert.IsType<Completed>(a[^1]);
        Assert.IsType<Completed>(o[^1]);
        // 兩邊的文字內容一致：同一段檔案快照
        using var anthropic = JsonDocument.Parse(anthropicHandler.LastRequestBody!);
        using var openAi = JsonDocument.Parse(openAiHandler.LastRequestBody!);
        var fileText = anthropic.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains(fileText, openAi.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }
}
