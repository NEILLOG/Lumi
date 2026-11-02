using System.Globalization;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Models;
using Lumi.Core.Prompts;

namespace Lumi.Core.Tests.Prompts;

/// <summary>第 7 層：近況清單的使用規則（M26）。</summary>
public sealed class RecentConversationsPromptLayerTests
{
    private static readonly RecentConversationsPromptLayer Layer = new();

    private static PromptContext Context(ChatMode mode = ChatMode.Workbench, bool on = true, ScheduledRunInfo? run = null) =>
        new(mode, PersonaSettings.Default, null, run, on);

    [Fact]
    public void On_RendersTheOnlyWhenRelevantRule()
    {
        var text = Layer.Render(Context());

        Assert.NotNull(text);
        Assert.Contains("只有相關才提及", text);
        Assert.Contains("<recent_conversations>", text);
        Assert.Contains("read_conversation", text);
        Assert.Equal(PromptPlacement.System, Layer.Placement);
    }

    [Fact]
    public void Off_RendersNothing() => Assert.Null(Layer.Render(Context(on: false)));

    [Fact]
    public void DefaultContext_RendersNothing() =>
        Assert.Null(Layer.Render(new PromptContext(ChatMode.Workbench, PersonaSettings.Default)));

    [Fact]
    public void ScheduledRun_RendersNothing()
    {
        var at = DateTimeOffset.Parse("2026-10-23T09:00+08:00", CultureInfo.InvariantCulture);

        Assert.Null(Layer.Render(Context(run: new ScheduledRunInfo("早報", at, at))));
    }

    [Fact]
    public void System_IsTheSameInBothModes() =>   // system 層不能依模式改內容（快取）
        Assert.Equal(Layer.Render(Context(ChatMode.Workbench)), Layer.Render(Context(ChatMode.Sprite)));

    [Fact]
    public void Formatter_SendsTheDigestAsIs()
    {
        const string content = "<recent_conversations>\n片段\n</recent_conversations>";
        var block = new ContextBlock(ContextKind.RecentConversations, "recent_conversations", "", content, true);

        Assert.Equal(content, ContextBlockFormatter.ToPromptText(block));
    }
}
