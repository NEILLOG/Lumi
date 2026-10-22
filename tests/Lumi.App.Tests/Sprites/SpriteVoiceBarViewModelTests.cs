using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Speech;

namespace Lumi.App.Tests.Sprites;

/// <summary>M6 6E：小精靈輸入列的語音部分（麥克風鈕的啟用與原因、收音列的狀態、隱藏時的收音、Esc、草稿標籤）。純邏輯，假的控制器。</summary>
public class SpriteVoiceBarViewModelTests
{
    private sealed class Rig
    {
        public FakeVoiceInput Voice { get; } = new();
        public SpriteVoiceBarViewModel Bar { get; }

        public Rig(bool shown = true)
        {
            Bar = new SpriteVoiceBarViewModel(Voice);
            Bar.Shown = shown;
        }
    }

    [Fact]
    public void MicrophoneIsEnabled_WhenShownAndAvailable()
    {
        var rig = new Rig();

        Assert.True(rig.Bar.MicEnabled);
        Assert.Equal(Strings.Composer_Mic, rig.Bar.MicTip);
    }

    [Fact]
    public void MicrophoneIsDisabled_WithTheReasonAsTooltip_WhenNotAvailable()
    {
        var rig = new Rig();

        rig.Voice.SetAvailability(false, Strings.Voice_NotConfigured);

        Assert.False(rig.Bar.MicEnabled);
        Assert.Equal(Strings.Voice_NotConfigured, rig.Bar.MicTip);
    }

    [Fact]
    public void MicrophoneIsDisabled_WhileTheSpriteIsHidden()
    {
        var rig = new Rig(shown: false);

        Assert.False(rig.Bar.MicEnabled);
        Assert.Equal(Strings.Sprite_VoiceHidden, rig.Bar.MicTip);

        rig.Bar.Shown = true;
        Assert.True(rig.Bar.MicEnabled);
    }

    [Fact]
    public void MicrophoneIsDisabled_WhenTheWorkbenchIsRecording_WithTheOtherWindowReason()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Workbench;
        rig.Voice.State = VoiceInputState.Recording;

        Assert.False(rig.Bar.MicEnabled);
        Assert.Equal(Strings.Voice_OtherWindow, rig.Bar.MicTip);
        Assert.True(rig.Bar.IsRecording);
    }

    [Fact]
    public void MicrophoneStaysEnabled_WhenTheSpriteItselfIsRecording()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;

        Assert.True(rig.Bar.IsVoiceActive);
        Assert.True(rig.Bar.IsRecording);
        Assert.Equal(Strings.Composer_Mic, rig.Bar.MicTip);   // 同一邊的錄音不是「另一個視窗」
    }

    [Fact]
    public void TheRecordingName_CarriesTheClock()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Voice.SetElapsed(TimeSpan.FromSeconds(12));

        Assert.Equal("00:12", rig.Bar.TimeText);
        Assert.Equal(string.Format(Strings.Voice_StatusRecording, "00:12"), rig.Bar.RecordingName);
    }

    [Fact]
    public void FailureText_IsASentence_WithTheServiceMessage()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.LastFailureMessage = "服務暫時無法使用";
        rig.Voice.State = VoiceInputState.Failed;

        Assert.True(rig.Bar.IsFailed);
        Assert.Equal(SpriteVoiceBarViewModel.FailureSentence("服務暫時無法使用"), rig.Bar.FailureText);
        Assert.StartsWith(Strings.Voice_FailedTitle.Substring(0, 2), rig.Bar.FailureText);
    }

    [Fact]
    public void HidingWhileRecordingFromTheSprite_CancelsTheRecording()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;

        rig.Bar.Shown = false;

        Assert.Contains("cancel", rig.Voice.Calls);
    }

    [Fact]
    public void HidingWhileTheWorkbenchRecords_DoesNotTouchTheRecording()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Workbench;
        rig.Voice.State = VoiceInputState.Recording;

        rig.Bar.Shown = false;

        Assert.DoesNotContain("cancel", rig.Voice.Calls);
    }

    [Fact]
    public void Escape_CancelsOnlyWhileRecording()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;

        Assert.False(rig.Bar.CancelRecordingIfAny());
        Assert.DoesNotContain("cancel", rig.Voice.Calls);

        rig.Voice.State = VoiceInputState.Recording;
        Assert.True(rig.Bar.CancelRecordingIfAny());
        Assert.Contains("cancel", rig.Voice.Calls);
    }

    [Fact]
    public void TheCommands_ForwardToTheController()
    {
        var rig = new Rig();

        rig.Bar.StartVoiceCommand.Execute(null);
        rig.Voice.State = VoiceInputState.Recording;
        rig.Bar.StopEditCommand.Execute(null);
        rig.Bar.StopSendCommand.Execute(null);
        rig.Bar.CancelCommand.Execute(null);
        rig.Voice.State = VoiceInputState.Failed;
        rig.Bar.RetryCommand.Execute(null);
        rig.Bar.DiscardCommand.Execute(null);

        Assert.Equal(["start", "stop:Edit", "stop:Send", "cancel", "retry", "discard"], rig.Voice.Calls);
        Assert.Equal(VoiceOrigin.Sprite, rig.Voice.Origin);   // 小精靈的麥克風開始的錄音，結果交回小精靈
    }

    [Fact]
    public void StartCommand_DoesNothing_WhenTheMicrophoneIsDisabled()
    {
        var rig = new Rig();
        rig.Voice.SetAvailability(false, Strings.Voice_TokenMissing);

        Assert.False(rig.Bar.StartVoiceCommand.CanExecute(null));
    }

    // ---- 語音草稿的標籤 ----

    [Fact]
    public void TheVoiceLabel_StaysUntilTheTextChanges()
    {
        var rig = new Rig();

        rig.Bar.MarkDraft("明天會下雨");
        Assert.True(rig.Bar.IsVoiceDraft);

        rig.Bar.OnInputEdited("明天會下雨");   // 同一段文字：標籤還在
        Assert.True(rig.Bar.IsVoiceDraft);

        rig.Bar.OnInputEdited("明天會下雨嗎");
        Assert.False(rig.Bar.IsVoiceDraft);
    }

    [Fact]
    public void ClearingTheDraft_RemovesTheLabel()
    {
        var rig = new Rig();
        rig.Bar.MarkDraft("你好");

        rig.Bar.OnInputEdited("");

        Assert.False(rig.Bar.IsVoiceDraft);
    }
}
