using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>M6 6E：語音輸入的狀態（Recording→Listening、Transcribing→Transcribing、Failed→Error），對話優先，以及退化交給 <see cref="SpriteStateResolver"/>。</summary>
public class SpriteVoiceStateTests
{
    private static SpriteStateMapper NewMapper() => new(new SpriteShellOptions(), new FakeTimeProvider());

    private static ChatRunResult Result(bool cancelled = false, ChatFailure? failure = null, bool unexpected = false) =>
        new("conv", failure, unexpected, cancelled);

    private static Completed Answer(string text) => new(
        new ChatMessage("m1", ChatRole.Assistant, [new TextBlock(text)], DateTimeOffset.UnixEpoch), StopReason.EndTurn);

    [Fact]
    public void Recording_IsListening_Transcribing_IsTranscribing_AndIdleReturnsToIdle()
    {
        var mapper = NewMapper();

        mapper.OnVoiceState(VoiceInputState.Recording);
        Assert.Equal(SpriteState.Listening, mapper.State);

        mapper.OnVoiceState(VoiceInputState.Transcribing);
        Assert.Equal(SpriteState.Transcribing, mapper.State);

        mapper.OnVoiceState(VoiceInputState.Idle);
        Assert.Equal(SpriteState.Idle, mapper.State);
    }

    [Fact]
    public void Failed_IsError_AndStaysUntilAcknowledged()
    {
        var mapper = NewMapper();
        mapper.OnVoiceState(VoiceInputState.Transcribing);

        mapper.OnVoiceState(VoiceInputState.Failed);
        Assert.Equal(SpriteState.Error, mapper.State);

        mapper.OnVoiceState(VoiceInputState.Failed);   // 重複的通知不會改變什麼
        Assert.Equal(SpriteState.Error, mapper.State);

        mapper.Acknowledge();   // 錯誤的氣泡被關掉
        Assert.Equal(SpriteState.Idle, mapper.State);
    }

    [Fact]
    public void Retry_GoesFromError_BackToTranscribing()
    {
        var mapper = NewMapper();
        mapper.OnVoiceState(VoiceInputState.Failed);

        mapper.OnVoiceState(VoiceInputState.Transcribing);

        Assert.Equal(SpriteState.Transcribing, mapper.State);
    }

    [Fact]
    public void VoiceIdle_DoesNotTouchAnErrorThatCameFromAChatRun()
    {
        var mapper = NewMapper();
        mapper.OnRunStarted();
        mapper.OnEvent(new Failed(new ChatFailure(ChatFailureKind.Network, "斷線", true)));
        mapper.OnRunFinished(Result(failure: new ChatFailure(ChatFailureKind.Network, "斷線", true)));

        mapper.OnVoiceState(VoiceInputState.Idle);

        Assert.Equal(SpriteState.Error, mapper.State);
    }

    [Fact]
    public void VoiceIdle_DoesNotOverwriteDone()
    {
        var mapper = NewMapper();
        mapper.OnRunStarted();
        mapper.OnEvent(Answer("好"));

        mapper.OnVoiceState(VoiceInputState.Idle);

        Assert.Equal(SpriteState.Done, mapper.State);
    }

    [Fact]
    public void WhileAChatRunIsActive_VoiceDoesNotReplaceTheChatState()
    {
        var mapper = NewMapper();
        mapper.OnRunStarted();

        mapper.OnVoiceState(VoiceInputState.Transcribing);   // 轉寫是在回應開始之前開始的
        Assert.Equal(SpriteState.Thinking, mapper.State);

        mapper.OnVoiceState(VoiceInputState.Idle);
        Assert.Equal(SpriteState.Thinking, mapper.State);
    }

    [Fact]
    public void AfterTheRunFinishes_VoiceCountsAgain()
    {
        var mapper = NewMapper();
        mapper.OnRunStarted();
        mapper.OnRunFinished(Result(cancelled: true));

        mapper.OnVoiceState(VoiceInputState.Recording);

        Assert.Equal(SpriteState.Listening, mapper.State);
    }

    // ---- 退化：由 SpriteStateResolver 負責（殼不自己退化） ----

    [Fact]
    public void ACharacterWithoutListening_ShowsTranscribingAsIdle()
    {
        // 只支援 Idle 的角色：Transcribing → Listening → Idle（預設退化規則）
        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(SpriteState.Transcribing, new BlockSpriteFactory().Manifest));
        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(SpriteState.Listening, new BlockSpriteFactory().Manifest));
        Assert.Equal(SpriteState.Idle, SpriteStateResolver.Resolve(SpriteState.Error, new BlockSpriteFactory().Manifest));
    }

    [Fact]
    public void ACharacterWithListeningOnly_ShowsTranscribingAsListening()
    {
        var manifest = new BlockSpriteFactory().Manifest with
        {
            Supported = new HashSet<SpriteState> { SpriteState.Idle, SpriteState.Listening },
        };

        Assert.Equal(SpriteState.Listening, SpriteStateResolver.Resolve(SpriteState.Transcribing, manifest));
    }
}
