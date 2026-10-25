using System.Text.Json;
using System.Text.Json.Nodes;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Providers.Anthropic;
using Lumi.Providers.OpenAICompatible;
using Lumi.Providers.Tests.TestSupport;
using Microsoft.Extensions.Options;

namespace Lumi.Providers.Tests;

/// <summary>M18 18L：工具結果附的圖片（瀏覽器截圖）在兩種格式裡的樣子。</summary>
public class ToolResultImageTests
{
    private static readonly ConnectionSettings AnthropicConnection = new("a", "A", ProviderFormat.Anthropic, "https://api.anthropic.test");
    private static readonly ConnectionSettings OpenAiConnection = new("o", "O", ProviderFormat.OpenAICompatible, "http://localhost:11434/v1/");

    private static ResolvedModel AnthropicModel => new(new ModelSettings("m1", "a", "claude-opus-5-5", "C", 1024, 1_000_000, true), AnthropicConnection);
    private static ResolvedModel OpenAiModel => new(new ModelSettings("m2", "o", "gpt-test", "G", 1024, 128_000, true), OpenAiConnection);

    private static readonly byte[] Jpeg1 = [0xFF, 0xD8, 0xFF, 1, 2, 3];
    private static readonly byte[] Jpeg2 = [0xFF, 0xD8, 0xFF, 9, 8, 7];

    private static AnthropicChatProvider Anthropic(FakeAnthropicHttpHandler handler, FakeAttachmentStore store) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.test/") }, FakeSecretStore.WithKey("sk-ant-test-FAKE-KEY-0000"),
        Options.Create(new AnthropicProviderOptions { UseServerFallbacks = false }), new CapturingLogger<AnthropicChatProvider>(), store);

    private static ChatMessage Msg(ChatRole role, params ContentBlock[] blocks) => new(ChatMessage.NewId(), role, blocks, DateTimeOffset.UtcNow);

    private static ImageBlock Shot(string file, string display = "截圖.jpg") => new(file, "image/jpeg", display);

    private static ToolResultBlock Result(string id, string text, params ImageBlock[]? images) =>
        new(id, text, false, "browser_screenshot", null, null, 1, ToolApproval.NotRequired, images);

    private static List<ChatMessage> History(params ToolResultBlock[] results) =>
    [
        Msg(ChatRole.User, new TextBlock("截圖給我看")),
        Msg(ChatRole.Assistant, results.Select(r => (ContentBlock)new ToolUseBlock(r.ToolUseId, "browser_screenshot", "{}")).ToArray()),
        Msg(ChatRole.Tool, results.Cast<ContentBlock>().ToArray()),
    ];

    private static async Task<JsonElement> AnthropicToolResultContent(FakeAttachmentStore store, params ToolResultBlock[] results)
    {
        var handler = FakeAnthropicHttpHandler.ForSse(Fixtures.Sse("basic-text"), 64);
        await foreach (var _ in Anthropic(handler, store).StreamAsync(new ChatRequest(AnthropicModel, History(results)))) { }
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");
        return messages[messages.GetArrayLength() - 1].GetProperty("content")[0].Clone();
    }

    // ================= Anthropic =================

    [Fact]
    public async Task Anthropic_ResultWithImages_HasTextThenImageBlocks_InOrder()
    {
        var store = new FakeAttachmentStore();
        store.Files["a.jpg"] = Jpeg1;
        store.Files["b.jpg"] = Jpeg2;

        var toolResult = await AnthropicToolResultContent(store, Result("t1", "已截圖", Shot("a.jpg"), Shot("b.jpg")));

        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("t1", toolResult.GetProperty("tool_use_id").GetString());
        var content = toolResult.GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal(["text", "image", "image"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("已截圖", content[0].GetProperty("text").GetString());
        Assert.Equal("base64", content[1].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/jpeg", content[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(Jpeg1), content[1].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal(Convert.ToBase64String(Jpeg2), content[2].GetProperty("source").GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_ResultWithoutImages_KeepsStringContent_IdenticalToBefore()
    {
        var store = new FakeAttachmentStore();

        var plain = await AnthropicToolResultContent(store, Result("t1", "結果文字", null));
        var emptyList = await AnthropicToolResultContent(store, Result("t1", "結果文字"));

        Assert.Equal(JsonValueKind.String, plain.GetProperty("content").ValueKind);
        Assert.Equal("結果文字", plain.GetProperty("content").GetString());
        Assert.Equal(plain.GetRawText(), emptyList.GetRawText());
    }

    [Fact]
    public async Task Anthropic_AllImageFilesMissing_FallsBackToStringContent()
    {
        var toolResult = await AnthropicToolResultContent(new FakeAttachmentStore(), Result("t1", "結果文字", Shot("gone.jpg")));

        Assert.Equal(JsonValueKind.String, toolResult.GetProperty("content").ValueKind);
        Assert.Equal("結果文字", toolResult.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Anthropic_OneImageMissing_BecomesATextNote_InPlace()
    {
        var store = new FakeAttachmentStore();
        store.Files["b.jpg"] = Jpeg2;

        var toolResult = await AnthropicToolResultContent(store, Result("t1", "已截圖", Shot("gone.jpg", "舊截圖.jpg"), Shot("b.jpg")));

        var content = toolResult.GetProperty("content");
        Assert.Equal(["text", "text", "image"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Contains("舊截圖.jpg", content[1].GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String(Jpeg2), content[2].GetProperty("source").GetProperty("data").GetString());
    }

    [Fact]
    public async Task Anthropic_ImagesAreLoadedFromToolResults_AndErrorFlagIsKept()
    {
        var store = new FakeAttachmentStore();
        store.Files["a.jpg"] = Jpeg1;
        var errored = Result("t1", "失敗但有圖", Shot("a.jpg")) with { IsError = true };

        var toolResult = await AnthropicToolResultContent(store, errored);

        Assert.True(toolResult.GetProperty("is_error").GetBoolean());
        Assert.Equal("image", toolResult.GetProperty("content")[1].GetProperty("type").GetString());
    }

    // ================= OpenAI 相容 =================

    private static JsonArray OpenAiMessages(IReadOnlyList<ChatMessage> history, IReadOnlyDictionary<string, byte[]> images, string? turnSuffix = null) =>
        JsonNode.Parse(OpenAICompatibleChatProvider.BuildBody(new ChatRequest(OpenAiModel, history) { TurnSuffix = turnSuffix }, images))!["messages"]!.AsArray();

    private static Dictionary<string, byte[]> Loaded(params (string Name, byte[] Data)[] files) =>
        files.ToDictionary(f => f.Name, f => f.Data, StringComparer.Ordinal);

    [Fact]
    public void OpenAi_ResultWithImages_AddsAUserMessageRightAfterTheToolMessage()
    {
        var messages = OpenAiMessages(History(Result("t1", "已截圖", Shot("a.jpg"))), Loaded(("a.jpg", Jpeg1)));

        Assert.Equal(["user", "assistant", "tool", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var parts = messages[3]!["content"]!.AsArray();
        Assert.Equal("text", parts[0]!["type"]!.GetValue<string>());
        Assert.Equal("（以上工具產生的圖片）", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("image_url", parts[1]!["type"]!.GetValue<string>());
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Jpeg1)}", parts[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_ToolMessageItself_StaysPlainText()
    {
        var messages = OpenAiMessages(History(Result("t1", "已截圖", Shot("a.jpg"))), Loaded(("a.jpg", Jpeg1)));

        var tool = messages[2]!;
        Assert.Equal("tool", tool["role"]!.GetValue<string>());
        Assert.Equal("t1", tool["tool_call_id"]!.GetValue<string>());
        Assert.Equal("已截圖", tool["content"]!.GetValue<string>());
        Assert.DoesNotContain("image", tool.ToJsonString());
    }

    [Fact]
    public void OpenAi_ImagesOfSeveralResults_AreMergedIntoOneUserMessage_InOrder()
    {
        var history = History(Result("t1", "一", Shot("a.jpg")), Result("t2", "二", Shot("b.jpg")));

        var messages = OpenAiMessages(history, Loaded(("a.jpg", Jpeg1), ("b.jpg", Jpeg2)));

        Assert.Equal(["user", "assistant", "tool", "tool", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        var parts = messages[4]!["content"]!.AsArray();
        Assert.Equal(3, parts.Count);
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Jpeg1)}", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal($"data:image/jpeg;base64,{Convert.ToBase64String(Jpeg2)}", parts[2]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_ResultsWithoutImages_RequestIsIdenticalToBefore()
    {
        var plain = History(Result("t1", "結果", null));
        var emptyList = History(Result("t1", "結果"));

        var messages = OpenAiMessages(plain, Loaded());

        Assert.Equal(["user", "assistant", "tool"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal(
            OpenAICompatibleChatProvider.BuildBody(new ChatRequest(OpenAiModel, plain), Loaded()).Replace("t1", "T"),
            OpenAICompatibleChatProvider.BuildBody(new ChatRequest(OpenAiModel, emptyList), Loaded()).Replace("t1", "T"));
    }

    [Fact]
    public void OpenAi_UnreadableImages_AreSkipped_AndNoUserMessageIsAdded()
    {
        var messages = OpenAiMessages(History(Result("t1", "已截圖", Shot("gone.jpg"))), Loaded());

        Assert.Equal(["user", "assistant", "tool"], messages.Select(m => m!["role"]!.GetValue<string>()));
    }

    [Fact]
    public void OpenAi_UnreadableImage_IsSkipped_ButTheReadableOneIsKept()
    {
        var messages = OpenAiMessages(History(Result("t1", "已截圖", Shot("gone.jpg"), Shot("b.jpg"))), Loaded(("b.jpg", Jpeg2)));

        var parts = messages[3]!["content"]!.AsArray();
        Assert.Equal(2, parts.Count);
        Assert.Contains(Convert.ToBase64String(Jpeg2), parts[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_ImageMessage_ComesBeforeTheTurnSuffixMessage()
    {
        var messages = OpenAiMessages(History(Result("t1", "已截圖", Shot("a.jpg"))), Loaded(("a.jpg", Jpeg1)), "模式指示");

        Assert.Equal(["user", "assistant", "tool", "user", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("模式指示", messages[4]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void OpenAi_ImageMessage_IsFlushedBeforeTheNextUserTurn()
    {
        var history = History(Result("t1", "已截圖", Shot("a.jpg")));
        history.Add(Msg(ChatRole.Assistant, new TextBlock("看到了")));
        history.Add(Msg(ChatRole.User, new TextBlock("謝謝")));

        var messages = OpenAiMessages(history, Loaded(("a.jpg", Jpeg1)));

        Assert.Equal(["user", "assistant", "tool", "user", "assistant", "user"], messages.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("謝謝", messages[5]!["content"]!.GetValue<string>());
    }

    // ================= 共用 =================

    [Fact]
    public async Task ImageAttachments_LoadsAndDetectsImagesOnToolResults()
    {
        var store = new FakeAttachmentStore();
        store.Files["a.jpg"] = Jpeg1;
        var history = History(Result("t1", "已截圖", Shot("a.jpg"), Shot("gone.jpg")));

        var loaded = await ImageAttachments.LoadAsync(store, history, CancellationToken.None);

        Assert.Equal(["a.jpg"], loaded.Keys);
        Assert.True(ImageAttachments.HasImages(new ChatRequest(AnthropicModel, history)));
        Assert.False(ImageAttachments.HasImages(new ChatRequest(AnthropicModel, History(Result("t1", "沒圖")))));
    }
}
