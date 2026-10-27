using Lumi.App.Resources;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;

namespace Lumi.App.Tests.Sprites;

/// <summary>M22 22I：小精靈氣泡在摘要呼叫進行中顯示「正在整理對話…」，結束（成功或失敗）後回到思考中。</summary>
public class SpriteCompactionBubbleTests
{
    private static SpriteBubbleViewModel Running()
    {
        var bubble = new SpriteBubbleViewModel(new SpriteShellOptions(), new FakeTimeProvider());
        bubble.OnRunStarted();
        return bubble;
    }

    [Fact]
    public void ASummaryCall_ShowsTheCompactingSentence_UntilItCompletes()
    {
        var bubble = Running();

        bubble.OnEvent(new CompactionStarted(CompactionKind.Summary));
        Assert.Equal(SpriteBubbleKind.Status, bubble.Kind);
        Assert.Equal(Strings.Compact_InProgress, bubble.StatusText);

        bubble.OnEvent(new CompactionCompleted(CompactionKind.Summary, UptoSeq: 2));
        Assert.Equal(Strings.Sprite_StatusThinking, bubble.StatusText);
    }

    [Fact]
    public void AFailedSummary_GoesBackToThinking()
    {
        var bubble = Running();

        bubble.OnEvent(new CompactionStarted(CompactionKind.Summary));
        bubble.OnEvent(new CompactionFailed(CompactionKind.Summary, new ChatFailure(ChatFailureKind.Network, "x", true)));

        Assert.Equal(SpriteBubbleKind.Status, bubble.Kind);
        Assert.Equal(Strings.Sprite_StatusThinking, bubble.StatusText);
    }

    [Fact]
    public void ToolResultCompaction_IsInstant_AndChangesNothing()
    {
        var bubble = Running();

        bubble.OnEvent(new CompactionStarted(CompactionKind.ToolResults));
        bubble.OnEvent(new CompactionCompleted(CompactionKind.ToolResults, UptoSeq: 4));

        Assert.Equal(Strings.Sprite_StatusThinking, bubble.StatusText);
    }
}
