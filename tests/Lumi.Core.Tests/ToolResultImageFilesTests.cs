using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests;

/// <summary>22F：工具結果的 <see cref="ToolResult.ImageFiles"/>（只給畫面顯示的圖片檔）存進結果區塊，舊資料沒有這個欄位時照常讀。</summary>
public class ToolResultImageFilesTests
{
    private static async Task<ToolResultBlock> RunToolAsync(ToolResult result)
    {
        var tool = new FakeTool("make") { Behavior = (_, _) => Task.FromResult(result) };
        var store = new InMemoryConversationStore();
        var provider = new SequenceChatProvider(
            SequenceChatProvider.ToolCalls(new ToolUseBlock("t1", "make", """{"text":"x"}""")), SequenceChatProvider.Answer("好"));
        var service = new ConversationService(provider, store, new FakeModelCatalog(), new ToolRegistry([tool]), new ScriptedApprovalService(),
            new ToolPermissionMemory(), new ToolLoopOptions(), TimeProvider.System, NullLogger<ConversationService>.Instance);
        var conversation = await store.CreateAsync("測試");

        var events = new List<ChatEvent>();
        await foreach (var e in service.SendAsync(conversation.Id, "畫圖"))
            events.Add(e);
        return Assert.Single(events.OfType<ToolCallCompleted>()).Result;
    }

    [Fact]
    public async Task ImageFiles_are_recorded_on_the_result_block_and_not_sent_as_images()
    {
        var block = await RunToolAsync(new ToolResult(false, "已產生圖片", "已產生圖片") { ImageFiles = ["/pics/Lumi/a.png"] });

        Assert.Equal(["/pics/Lumi/a.png"], block.ImageFiles);
        Assert.Null(block.Images);
        Assert.Equal("已產生圖片", block.Content);
    }

    [Fact]
    public async Task Without_ImageFiles_the_block_keeps_null()
    {
        var block = await RunToolAsync(ToolResult.Ok("沒有圖"));

        Assert.Null(block.ImageFiles);
    }

    [Fact]
    public void Block_round_trips_through_json_and_old_json_without_the_field_reads_as_null()
    {
        var block = new ToolResultBlock("t1", "好", false, "產生圖片", null, "已產生圖片", 5, ToolApproval.AllowedOnce, null, ["/a.png"]);
        var json = JsonSerializer.Serialize<ContentBlock>(block);

        var back = Assert.IsType<ToolResultBlock>(JsonSerializer.Deserialize<ContentBlock>(json));
        Assert.Equal(["/a.png"], back.ImageFiles);

        const string old = """{"type":"tool_result","toolUseId":"t1","content":"好","isError":false,"displayName":"讀檔","target":null,"summary":null,"durationMs":3,"approval":"NotRequired"}""";
        var legacy = Assert.IsType<ToolResultBlock>(JsonSerializer.Deserialize<ContentBlock>(old));
        Assert.Null(legacy.ImageFiles);
        Assert.Null(legacy.Images);
    }
}

public class ImageGenerationSettingsTests
{
    [Fact]
    public void Default_is_not_configured_with_square_size()
    {
        Assert.False(ImageGenerationSettings.Default.IsConfigured);
        Assert.Equal("1024x1024", ImageGenerationSettings.Default.Size);
    }

    [Fact]
    public void Configured_needs_both_connection_and_model()
    {
        Assert.True(new ImageGenerationSettings("c1", "gpt-image-2").IsConfigured);
        Assert.False(new ImageGenerationSettings("c1", null).IsConfigured);
        Assert.False(new ImageGenerationSettings(null, "gpt-image-2").IsConfigured);
        Assert.False(new ImageGenerationSettings("  ", "gpt-image-2").IsConfigured);
    }

    [Fact]
    public void Normalize_trims_blanks_to_null_and_unknown_size_becomes_default()
    {
        var normalized = new ImageGenerationSettings(" c1 ", "  ", "999x999").Normalize();

        Assert.Equal("c1", normalized.ConnectionId);
        Assert.Null(normalized.ModelId);
        Assert.Equal("1024x1024", normalized.Size);
        Assert.Equal("1536x1024", new ImageGenerationSettings("c1", "m", "1536x1024").Normalize().Size);
    }

    [Fact]
    public void IsConfigured_is_not_written_to_json()
    {
        var json = JsonSerializer.Serialize(new ImageGenerationSettings("c1", "m"));

        Assert.DoesNotContain("isConfigured", json, StringComparison.OrdinalIgnoreCase);
    }
}
