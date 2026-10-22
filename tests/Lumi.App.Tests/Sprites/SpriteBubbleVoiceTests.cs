using Lumi.App.Resources;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;

namespace Lumi.App.Tests.Sprites;

/// <summary>M6 6E：氣泡的語音部分——送出時的小字引用、語音提示（自動淡出）、語音轉寫失敗（不淡出，重試與放棄）。</summary>
public class SpriteBubbleVoiceTests
{
    private sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new();
        public SpriteBubbleViewModel Bubble { get; }
        public int Dismissals { get; private set; }
        public int Retries { get; private set; }
        public int Discards { get; private set; }

        public Rig()
        {
            Bubble = new SpriteBubbleViewModel(new SpriteShellOptions(), Time);
            Bubble.Dismissed += () => Dismissals++;
            Bubble.VoiceRetryRequested += () => Retries++;
            Bubble.VoiceDiscardRequested += () => Discards++;
        }

        public void Tick(double seconds)
        {
            Time.Advance(TimeSpan.FromSeconds(seconds));
            Bubble.Tick();
        }
    }

    // ---- 引用 ----

    [Fact]
    public void ASpokenQuestion_IsQuotedAboveTheThinkingLine()
    {
        var rig = new Rig();

        rig.Bubble.OnRunStarted("明天台中會下雨嗎？");

        Assert.Equal("明天台中會下雨嗎？", rig.Bubble.QuoteText);
        Assert.True(rig.Bubble.HasQuote);
        Assert.Equal(SpriteBubbleKind.Status, rig.Bubble.Kind);
        Assert.Equal(Strings.Sprite_StatusThinking, rig.Bubble.StatusText);
    }

    [Fact]
    public void ALongQuote_IsTruncated_ByGraphemeCluster()
    {
        var rig = new Rig();
        var longText = new string('字', 200);

        rig.Bubble.OnRunStarted(longText);

        Assert.EndsWith(Strings.Sprite_Ellipsis, rig.Bubble.QuoteText);
        Assert.True(rig.Bubble.QuoteText!.Length < longText.Length);
    }

    [Fact]
    public void TheQuote_GoesAway_WhenTheAnswerStarts()
    {
        var rig = new Rig();
        rig.Bubble.OnRunStarted("明天台中會下雨嗎？");

        rig.Bubble.OnEvent(new TextDelta("會"));

        Assert.Null(rig.Bubble.QuoteText);
    }

    [Fact]
    public void AnOrdinaryRun_HasNoQuote()
    {
        var rig = new Rig();

        rig.Bubble.OnRunStarted();

        Assert.False(rig.Bubble.HasQuote);
    }

    // ---- 語音提示 ----

    [Fact]
    public void ANotice_ShowsAsAShortLine_ThatFadesWithoutTellingTheCharacter()
    {
        var rig = new Rig();

        rig.Bubble.ShowNotice(Strings.Voice_TooShort);

        Assert.True(rig.Bubble.IsNotice);
        Assert.True(rig.Bubble.IsVisible);
        Assert.False(rig.Bubble.IsInteractive);   // 提示不攔截滑鼠
        Assert.Equal(Strings.Voice_TooShort, rig.Bubble.NoticeText);

        rig.Tick(3);
        Assert.True(rig.Bubble.IsNotice);

        rig.Tick(2);
        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(0, rig.Dismissals);   // 提示淡出不通知角色
    }

    [Fact]
    public void ANotice_WaitsWhileTheCursorIsOnIt()
    {
        var rig = new Rig();
        rig.Bubble.ShowNotice(Strings.Voice_Silent);
        rig.Bubble.SetHovered(true);

        rig.Tick(10);

        Assert.True(rig.Bubble.IsNotice);
    }

    [Fact]
    public void ANotice_DoesNotCoverAnApprovalOrAnError()
    {
        var rig = new Rig();
        rig.Bubble.ShowVoiceFailure("失敗");

        rig.Bubble.ShowNotice(Strings.Voice_NoSpeech);

        Assert.True(rig.Bubble.IsError);
        Assert.False(rig.Bubble.IsNotice);
    }

    // ---- 語音轉寫失敗 ----

    [Fact]
    public void AVoiceFailure_IsAnErrorBubble_WithTheRetryAndDiscardButtons_AndDoesNotFade()
    {
        var rig = new Rig();

        rig.Bubble.ShowVoiceFailure(SpriteVoiceBarViewModel.FailureSentence("服務逾時"));

        Assert.True(rig.Bubble.IsError);
        Assert.True(rig.Bubble.IsVoiceFailure);
        Assert.True(rig.Bubble.IsInteractive);
        Assert.False(rig.Bubble.CanRetry);   // 聊天的重試不適用

        rig.Tick(600);
        Assert.True(rig.Bubble.IsError);
    }

    [Fact]
    public void RetryAndDiscard_RaiseTheirEvents()
    {
        var rig = new Rig();
        rig.Bubble.ShowVoiceFailure("失敗");

        rig.Bubble.VoiceRetryCommand.Execute(null);
        rig.Bubble.VoiceDiscardCommand.Execute(null);

        Assert.Equal(1, rig.Retries);
        Assert.Equal(1, rig.Discards);
        Assert.Equal(0, rig.Dismissals);
    }

    [Fact]
    public void HideVoiceFailure_ClearsOnlyTheVoiceError()
    {
        var rig = new Rig();
        rig.Bubble.ShowVoiceFailure("失敗");

        rig.Bubble.HideVoiceFailure();

        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(1, rig.Dismissals);   // 角色離開 Error（和錯誤氣泡被關掉相同）
    }

    [Fact]
    public void HideVoiceFailure_LeavesAChatError_Alone()
    {
        var rig = new Rig();
        rig.Bubble.OnRunFinished(new ChatRunResult("conv", new ChatFailure(ChatFailureKind.Network, "斷線", true), false, false));
        Assert.True(rig.Bubble.IsError);

        rig.Bubble.HideVoiceFailure();

        Assert.True(rig.Bubble.IsError);
        Assert.Equal(0, rig.Dismissals);
    }

    [Fact]
    public void AVoiceFailure_WaitsBehindAnApproval()
    {
        var rig = new Rig();
        rig.Bubble.OnEvent(new ApprovalRequired(new Lumi.Core.Tools.ToolApprovalRequest(
            "c1", "save", "儲存", Lumi.Core.Tools.ToolPermission.Write, new Lumi.Core.Tools.ToolPreview(null, null), "{}")));

        rig.Bubble.ShowVoiceFailure("失敗");

        Assert.True(rig.Bubble.IsApproval);
    }
}
