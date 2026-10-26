using System.Globalization;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.App.Tests;

/// <summary>M21 21C：對話匯出成 Markdown 的各區塊（純函式，不碰檔案）。</summary>
public class ConversationMarkdownExporterTests
{
    private static readonly DateTimeOffset ExportedAt = new(2026, 10, 27, 8, 30, 0, TimeSpan.Zero);

    private static readonly string ExportedLine =
        "匯出時間：" + ExportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static ChatMessage User(params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), ChatRole.User, blocks, ExportedAt);

    private static ChatMessage Assistant(params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), ChatRole.Assistant, blocks, ExportedAt);

    private static ChatMessage ToolResult(params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), ChatRole.Tool, blocks, ExportedAt);

    private static ToolResultBlock Result(string name, string? target, bool isError) =>
        new("call-1", "content", isError, name, target, null, 12, ToolApproval.NotRequired);

    [Fact]
    public void Header_HasTheTitleAndTheExportTime_AndNoSectionsWhenThereAreNoMessages()
    {
        var markdown = ConversationMarkdownExporter.Export("週報", [], ExportedAt);

        Assert.Equal($"# 週報\n\n{ExportedLine}\n", markdown);
    }

    [Fact]
    public void UserAndAssistantMessages_GetTheirHeadings_AndTextIsKeptVerbatim()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            User(new TextBlock("第一行\n\n第二行 **粗體**")),
            Assistant(new TextBlock("好的")),
        ], ExportedAt);

        Assert.Contains("## 你\n\n第一行\n\n第二行 **粗體**", markdown);
        Assert.Contains("## Lumi\n\n好的", markdown);
        Assert.True(markdown.IndexOf("## 你", StringComparison.Ordinal) < markdown.IndexOf("## Lumi", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsecutiveAssistantAndToolMessages_ShareOneLumiSection()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            User(new TextBlock("讀檔")),
            Assistant(new ToolUseBlock("call-1", "read_file", "{}")),   // 只有工具呼叫：不輸出
            ToolResult(Result("讀取檔案", "a.txt", isError: false)),
            Assistant(new TextBlock("內容如下")),
        ], ExportedAt);

        Assert.Equal(1, CountOccurrences(markdown, "## Lumi"));
        Assert.Contains("## Lumi\n\n> 工具：讀取檔案 a.txt（成功）\n\n內容如下", markdown);
    }

    [Fact]
    public void ToolResults_BecomeOneQuoteLine_WithSuccessOrFailure_AndNoTargetWhenThereIsNone()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            ToolResult(Result("執行指令", null, isError: true)),
        ], ExportedAt);

        Assert.Contains("> 工具：執行指令（失敗）", markdown);
    }

    [Fact]
    public void OnlyToolCalls_ProduceNoSection()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            Assistant(new ToolUseBlock("call-1", "read_file", "{}")),
        ], ExportedAt);

        Assert.DoesNotContain("## Lumi", markdown);
        Assert.DoesNotContain("## 你", markdown);
    }

    [Fact]
    public void Thinking_AndProviderBlocks_AreSkipped()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            Assistant(
                new ThinkingBlock("內部想法", "sig"),
                new RedactedThinkingBlock("opaque"),
                new ProviderBlock("anthropic", "server_tool", "{}"),
                new TextBlock("可見的回答")),
        ], ExportedAt);

        Assert.DoesNotContain("內部想法", markdown);
        Assert.DoesNotContain("opaque", markdown);
        Assert.DoesNotContain("server_tool", markdown);
        Assert.Contains("## Lumi\n\n可見的回答", markdown);
    }

    [Fact]
    public void ImagesAndAttachedContext_AreNamedOnly()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            User(
                new ContextBlock(ContextKind.File, "notes.txt", "/x/notes.txt", "秘密內容不匯出嗎？", Embedded: true),
                new ImageBlock("abc.png", "image/png", "截圖.png"),
                new TextBlock("看這個")),
        ], ExportedAt);

        Assert.Contains("[附檔：notes.txt]", markdown);
        Assert.Contains("[圖片：截圖.png]", markdown);
        Assert.Contains("看這個", markdown);
    }

    [Fact]
    public void EmptyMessages_AreSkipped()
    {
        var markdown = ConversationMarkdownExporter.Export("標題", [
            User(new TextBlock("")),
            Assistant(),
        ], ExportedAt);

        Assert.DoesNotContain("## ", markdown);
    }

    [Theory]
    [InlineData("a/b", "ab.md")]          // 路徑分隔字元在各平台都不允許
    [InlineData("  週報.  ", "週報.md")]
    [InlineData("/", "新對話.md")]        // 清掉之後是空的就用預設名稱
    [InlineData("", "新對話.md")]
    public void SuggestedFileName_RemovesInvalidCharacters_AndFallsBack(string title, string expected) =>
        Assert.Equal(expected, ConversationMarkdownExporter.SuggestedFileName(title));

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
