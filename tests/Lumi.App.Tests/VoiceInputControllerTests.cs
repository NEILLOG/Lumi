using System.ComponentModel;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M6 6D：語音輸入控制器的狀態機（不碰畫面，用假的錄音器、假的轉寫、假的時鐘與同步的 dispatcher）。</summary>
public class VoiceInputControllerTests
{
    private static readonly SpeechSettings Configured = new("https://speech.example.com/v1", "zh", 120, "abcd", "speech.example.com");

    private sealed class Rig : IAsyncDisposable
    {
        public FakeAudioRecorder Recorder { get; } = new();
        public FakeSpeechToText Speech { get; } = new();
        public FakeModelCatalog Catalog { get; } = new();
        public FakeChatSession Session { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public List<VoiceTranscribedArgs> Transcribed { get; } = [];
        public List<string> Changed { get; } = [];
        public VoiceInputController Controller { get; }

        public Rig(bool configured = true)
        {
            if (configured)
                Catalog.Current = Catalog.Current with { Speech = Configured };
            SetRecording();   // 預設是 2 秒、有聲音的錄音（假錄音器的預設是靜音）
            Controller = new VoiceInputController(Recorder, Speech, Catalog, Session, new SyncUiDispatcher(), Time, NullLogger<VoiceInputController>.Instance);
            Controller.Transcribed += (_, args) => Transcribed.Add(args);
            Controller.PropertyChanged += (_, e) => Changed.Add(e.PropertyName ?? "");
        }

        /// <summary>錄到一段（預設 2 秒、有聲音）的錄音結果。</summary>
        public void SetRecording(TimeSpan? length = null, bool tooShort = false, bool silent = false)
        {
            var d = length ?? TimeSpan.FromSeconds(2);
            Recorder.StopResult = new RecordedAudio(WavFormat.Silence(d), d, tooShort, silent);
        }

        public async ValueTask DisposeAsync() => await Controller.DisposeAsync();
    }

    // ---- 誰開始的錄音（M6 6E） ----

    [Fact]
    public async Task TheResult_IsTaggedWithTheSideThatStartedTheRecording()
    {
        await using var rig = new Rig();
        rig.Speech.Result = new("小精靈的話", "zh", TimeSpan.FromSeconds(2));

        await rig.Controller.StartAsync(VoiceOrigin.Sprite);
        Assert.Equal(VoiceOrigin.Sprite, rig.Controller.Origin);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceOrigin.Sprite, Assert.Single(rig.Transcribed).Origin);
    }

    [Fact]
    public async Task WhileTheOtherSideIsRecording_ItsReasonIsAnotherWindow_AndOwnReasonIsBusy()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Sprite);

        Assert.Equal(Strings.Voice_OtherWindow, rig.Controller.DisabledReasonFor(VoiceOrigin.Workbench));
        Assert.Equal(Strings.Voice_BusyReason, rig.Controller.DisabledReasonFor(VoiceOrigin.Sprite));
        Assert.False(rig.Controller.CanStart);
    }

    [Fact]
    public async Task WhileTheOtherSideIsTranscribing_ItsReasonIsAnotherWindow()
    {
        await using var rig = new Rig();
        rig.Speech.Hang = true;
        await rig.Controller.StartAsync(VoiceOrigin.Sprite);
        var stopping = rig.Controller.StopAsync(VoiceStopAction.Edit);
        Assert.Equal(VoiceInputState.Transcribing, rig.Controller.State);

        Assert.Equal(Strings.Voice_OtherWindow, rig.Controller.DisabledReasonFor(VoiceOrigin.Workbench));

        rig.Controller.Cancel();
        await stopping;
    }

    [Fact]
    public async Task WhenIdle_EachSideGetsTheSameReason()
    {
        await using var rig = new Rig(configured: false);

        Assert.Equal(rig.Controller.DisabledReason, rig.Controller.DisabledReasonFor(VoiceOrigin.Workbench));
        Assert.Equal(rig.Controller.DisabledReason, rig.Controller.DisabledReasonFor(VoiceOrigin.Sprite));
        Assert.Null(rig.Controller.Origin);
    }

    // ---- 正常流程 ----

    [Fact]
    public async Task Edit_RecordsTranscribes_AndHandsTheTextToTheCaller()
    {
        await using var rig = new Rig();
        rig.Session.ConversationId = "conv-7";
        rig.Speech.Result = new("  今天天氣不錯  ", "zh", TimeSpan.FromSeconds(2));

        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        Assert.Equal(VoiceInputState.Recording, rig.Controller.State);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        var sent = Assert.Single(rig.Transcribed);
        Assert.Equal("今天天氣不錯", sent.Text);   // 結果 trim 過
        Assert.Equal(VoiceStopAction.Edit, sent.Action);
        Assert.Equal("conv-7", sent.ConversationId);
        Assert.Equal("zh", sent.Language);
        var call = Assert.Single(rig.Speech.Calls);
        Assert.Equal("voice.wav", call.FileName);
        Assert.Equal("conv-7", call.SessionId);
        Assert.True(call.WavBytes > 44);   // 有檔頭與資料
        Assert.Null(rig.Controller.RecordedDuration);
    }

    [Fact]
    public async Task Send_IsHandedToTheCallerWithTheSendAction()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Send);

        Assert.Equal(VoiceStopAction.Send, Assert.Single(rig.Transcribed).Action);
    }

    [Fact]
    public async Task Transcribing_KeepsTheRecordedDuration_ForTheUi()
    {
        await using var rig = new Rig();
        rig.SetRecording(TimeSpan.FromSeconds(12));
        rig.Speech.Hang = true;
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        var stop = rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Transcribing, rig.Controller.State);
        Assert.Equal(TimeSpan.FromSeconds(12), rig.Controller.RecordedDuration);
        rig.Controller.Cancel();
        await stop;
    }

    // ---- 取消 ----

    [Fact]
    public async Task Cancel_WhileRecording_StopsTheRecorder_AndReturnsToIdle()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        rig.Controller.Cancel();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.False(rig.Recorder.IsRecording);
        Assert.Equal(1, rig.Recorder.CancelCount);
        Assert.Empty(rig.Speech.Calls);
        Assert.Empty(rig.Transcribed);
    }

    [Fact]
    public async Task Cancel_WhileTranscribing_CancelsTheRequest_AndDropsTheRecording()
    {
        await using var rig = new Rig();
        rig.Speech.Hang = true;
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        var stop = rig.Controller.StopAsync(VoiceStopAction.Edit);
        Assert.Equal(VoiceInputState.Transcribing, rig.Controller.State);

        rig.Controller.Cancel();
        await stop;

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Speech.CanceledCount);
        Assert.Empty(rig.Transcribed);
        Assert.Null(rig.Controller.LastFailureMessage);
        Assert.Null(rig.Controller.Notice);   // 使用者取消是靜默的
    }

    [Fact]
    public async Task Cancel_WhileIdle_DoesNothing()
    {
        await using var rig = new Rig();

        rig.Controller.Cancel();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(0, rig.Recorder.CancelCount);
    }

    // ---- 太短、靜音、空白結果 ----

    [Fact]
    public async Task TooShortRecording_IsIgnored_WithANotice_AndNothingIsSent()
    {
        await using var rig = new Rig();
        rig.SetRecording(TimeSpan.FromSeconds(0.2), tooShort: true);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        await rig.Controller.StopAsync(VoiceStopAction.Send);

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(Strings.Voice_TooShort, rig.Controller.Notice);
        Assert.Empty(rig.Speech.Calls);
        Assert.Empty(rig.Transcribed);
    }

    [Fact]
    public async Task SilentRecording_IsNotSent_AndAsksForTheMicrophonePermission()
    {
        await using var rig = new Rig();
        rig.SetRecording(silent: true);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(Strings.Voice_Silent, rig.Controller.Notice);
        Assert.Empty(rig.Speech.Calls);
    }

    [Fact]
    public async Task BlankResult_ShowsNoSpeechNotice_AndReturnsToIdle()
    {
        await using var rig = new Rig();
        rig.Speech.Result = new("   ", null, null);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(Strings.Voice_NoSpeech, rig.Controller.Notice);
        Assert.Empty(rig.Transcribed);
    }

    [Fact]
    public async Task ClearNotice_RemovesTheNotice()
    {
        await using var rig = new Rig();
        rig.SetRecording(TimeSpan.FromSeconds(0.1), tooShort: true);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        rig.Controller.ClearNotice();

        Assert.Null(rig.Controller.Notice);
    }

    [Fact]
    public async Task StartingANewRecording_ClearsTheOldNotice()
    {
        await using var rig = new Rig();
        rig.SetRecording(TimeSpan.FromSeconds(0.1), tooShort: true);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);
        Assert.NotNull(rig.Controller.Notice);

        rig.SetRecording();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        Assert.Null(rig.Controller.Notice);
    }

    // ---- 失敗、重試、放棄 ----

    [Fact]
    public async Task SpeechFailure_KeepsTheRecording_AndShowsTheServiceMessage()
    {
        await using var rig = new Rig();
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.ServerTimeout, "服務逾時，請稍後再試。", retryable: true));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Failed, rig.Controller.State);
        Assert.Equal("服務逾時，請稍後再試。", rig.Controller.LastFailureMessage);
        Assert.Empty(rig.Transcribed);
        Assert.False(rig.Controller.CanStart);
    }

    [Fact]
    public async Task RetryAfterFailure_TranscribesTheSameRecording_AndDeliversTheResult()
    {
        await using var rig = new Rig();
        rig.Session.ConversationId = "conv-3";
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.Network, "網路中斷", retryable: true));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Send);
        Assert.Equal(VoiceInputState.Failed, rig.Controller.State);

        await rig.Controller.RetryAsync();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Null(rig.Controller.LastFailureMessage);
        Assert.Equal(2, rig.Speech.Calls.Count);
        Assert.Equal(rig.Speech.Calls[0].WavBytes, rig.Speech.Calls[1].WavBytes);   // 同一段錄音
        Assert.Equal("conv-3", rig.Speech.Calls[1].SessionId);
        var sent = Assert.Single(rig.Transcribed);
        Assert.Equal(VoiceStopAction.Send, sent.Action);   // 沿用原本的動作
    }

    [Fact]
    public async Task Discard_AfterFailure_ReturnsToIdle_WithoutTranscribingAgain()
    {
        await using var rig = new Rig();
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.ServerError, "服務錯誤", retryable: true));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        rig.Controller.Discard();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Null(rig.Controller.LastFailureMessage);
        Assert.Single(rig.Speech.Calls);
        Assert.Empty(rig.Transcribed);
        await rig.Controller.RetryAsync();   // 放棄後沒有東西可重試
        Assert.Single(rig.Speech.Calls);
    }

    [Fact]
    public async Task CancelWhileFailed_DiscardsTheRecording()
    {
        await using var rig = new Rig();
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.Unknown, "失敗", retryable: false));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        rig.Controller.Cancel();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Null(rig.Controller.LastFailureMessage);
    }

    [Fact]
    public async Task UnexpectedError_BecomesFailed_WithAFixedMessage_AndDoesNotLeakTheException()
    {
        await using var rig = new Rig();
        rig.Speech.Failures.Enqueue(new InvalidOperationException("secret-token-leak"));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Failed, rig.Controller.State);
        Assert.Equal(Strings.Voice_TranscribeUnexpected, rig.Controller.LastFailureMessage);
        Assert.DoesNotContain("secret-token-leak", rig.Controller.LastFailureMessage);
    }

    [Fact]
    public async Task StopWhenIdle_DoesNothing()
    {
        await using var rig = new Rig();

        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Empty(rig.Speech.Calls);
        Assert.Equal(0, rig.Recorder.StopCount);
    }

    // ---- 錄音器的錯誤與 90 秒上限 ----

    [Fact]
    public async Task StartFailure_ShowsTheRecorderMessage_AndStaysIdle()
    {
        await using var rig = new Rig();
        rig.Recorder.StartFailure = new AudioRecorderException(AudioRecorderFailureKind.PermissionDenied, "麥克風沒有權限，請到系統設定開啟。");

        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal("麥克風沒有權限，請到系統設定開啟。", rig.Controller.Notice);
        Assert.True(rig.Controller.CanStart);   // 下次還能再試
    }

    [Fact]
    public async Task ReachingTheLimit_StopsAndEdits_Automatically()
    {
        await using var rig = new Rig();
        rig.SetRecording(RecordingLimits.MaxDuration);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        rig.Recorder.RaiseMaxDurationReached();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        var sent = Assert.Single(rig.Transcribed);
        Assert.Equal(VoiceStopAction.Edit, sent.Action);
        Assert.Equal(1, rig.Recorder.StopCount);
    }

    // ---- 能不能開始 ----

    [Fact]
    public async Task WithoutAnEndpoint_TheReasonPointsToSettings()
    {
        await using var rig = new Rig(configured: false);

        Assert.False(rig.Controller.CanStart);
        Assert.Equal(Strings.Voice_NotConfigured, rig.Controller.DisabledReason);
    }

    [Fact]
    public async Task AnInvalidEndpoint_IsReportedAsSuch()
    {
        await using var rig = new Rig();
        rig.Catalog.Current = rig.Catalog.Current with { Speech = Configured with { Endpoint = "http://speech.example.com/v1" } };

        Assert.Equal(Strings.Voice_EndpointInvalid, rig.Controller.DisabledReason);
    }

    [Fact]
    public async Task WithoutAToken_TheReasonSaysTheTokenIsMissing()
    {
        await using var rig = new Rig();
        rig.Catalog.Current = rig.Catalog.Current with { Speech = Configured with { TokenHint = null, TokenHost = null } };

        Assert.Equal(Strings.Voice_TokenMissing, rig.Controller.DisabledReason);
    }

    [Fact]
    public async Task WhenTheEndpointHostChanged_TheTokenCountsAsMissing()
    {
        await using var rig = new Rig();
        rig.Catalog.Current = rig.Catalog.Current with { Speech = Configured with { TokenHost = "old.example.com" } };

        Assert.Equal(Strings.Voice_TokenMissing, rig.Controller.DisabledReason);
        Assert.False(rig.Controller.CanStart);
    }

    [Fact]
    public async Task WhenTheSettingsChange_TheAvailabilityFollows()
    {
        await using var rig = new Rig();
        Assert.True(rig.Controller.CanStart);
        rig.Changed.Clear();

        rig.Catalog.Current = rig.Catalog.Current with { Speech = Configured with { Endpoint = "" } };
        rig.Catalog.Raise();

        Assert.False(rig.Controller.CanStart);
        Assert.Equal(Strings.Voice_NotConfigured, rig.Controller.DisabledReason);
        Assert.Contains(nameof(IVoiceInputController.CanStart), rig.Changed);
        Assert.Contains(nameof(IVoiceInputController.DisabledReason), rig.Changed);
    }

    [Fact]
    public async Task WhileResponding_VoiceCannotStart_AndTheReasonSaysSo()
    {
        await using var rig = new Rig();
        rig.Session.IsResponding = true;

        Assert.False(rig.Controller.CanStart);
        Assert.Equal(Strings.Voice_RespondingBlocked, rig.Controller.DisabledReason);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(0, rig.Recorder.StartCount);
    }

    [Fact]
    public async Task WhileRecordingOrTranscribing_TheReasonIsBusy_AndStartDoesNothing()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        Assert.Equal(Strings.Voice_BusyReason, rig.Controller.DisabledReason);
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        Assert.Equal(1, rig.Recorder.StartCount);

        rig.Speech.Hang = true;
        var stop = rig.Controller.StopAsync(VoiceStopAction.Edit);
        Assert.Equal(Strings.Voice_BusyReason, rig.Controller.DisabledReason);
        rig.Controller.Cancel();
        await stop;
    }

    [Fact]
    public async Task AfterFailure_TheReasonSaysTheRecordingIsStillPending()
    {
        await using var rig = new Rig();
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.Network, "失敗", retryable: true));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        Assert.Equal(Strings.Voice_FailedBlocked, rig.Controller.DisabledReason);
        rig.Controller.Discard();
        Assert.True(rig.Controller.CanStart);
    }

    // ---- 對話切換、回應開始 ----

    [Fact]
    public async Task RunStarted_WhileRecording_CancelsTheRecording()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        rig.Session.StartRun();

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.False(rig.Recorder.IsRecording);
        Assert.Empty(rig.Transcribed);
    }

    [Fact]
    public async Task ConversationChange_WhileRecording_CancelsTheRecording()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        rig.Session.SwitchConversation("conv-2");

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.False(rig.Recorder.IsRecording);
    }

    [Fact]
    public async Task ConversationChange_WhileTranscribing_CancelsTheRequest()
    {
        await using var rig = new Rig();
        rig.Speech.Hang = true;
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        var stop = rig.Controller.StopAsync(VoiceStopAction.Edit);

        rig.Session.SwitchConversation("conv-2");
        await stop;

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Speech.CanceledCount);
        Assert.Empty(rig.Transcribed);
    }

    [Fact]
    public async Task ConversationChange_WhileFailed_DropsTheRecording()
    {
        await using var rig = new Rig();
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.Network, "失敗", retryable: true));
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        await rig.Controller.StopAsync(VoiceStopAction.Edit);

        rig.Session.SwitchConversation("conv-2");

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.True(rig.Controller.CanStart);
    }

    [Fact]
    public async Task RunFinished_WhileRecording_DoesNotCancel()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        rig.Session.FinishRun();

        Assert.Equal(VoiceInputState.Recording, rig.Controller.State);
    }

    // ---- 計時與音量 ----

    [Fact]
    public async Task Elapsed_FollowsTheClock_AndNotifiesWhileRecording()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        Assert.Equal(TimeSpan.Zero, rig.Controller.Elapsed);
        rig.Changed.Clear();

        rig.Time.Advance(TimeSpan.FromSeconds(2.5));

        Assert.Equal(TimeSpan.FromSeconds(2.5), rig.Controller.Elapsed);
        Assert.Contains(nameof(IVoiceInputController.Elapsed), rig.Changed);
        rig.Controller.Cancel();
        Assert.Equal(TimeSpan.Zero, rig.Controller.Elapsed);
    }

    [Fact]
    public async Task Level_FollowsTheRecorder_ButIsThrottled()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        rig.Recorder.RaiseLevel(0.5f);
        Assert.Equal(0.5, rig.Controller.Level, 3);

        rig.Recorder.RaiseLevel(0.9f);   // 距離上一次不到 100 ms：忽略
        Assert.Equal(0.5, rig.Controller.Level, 3);

        rig.Time.Advance(TimeSpan.FromMilliseconds(150));
        rig.Recorder.RaiseLevel(0.9f);
        Assert.Equal(0.9, rig.Controller.Level, 3);

        rig.Recorder.RaiseLevel(2f);   // 超出範圍時夾到 1
        rig.Time.Advance(TimeSpan.FromMilliseconds(150));
        rig.Recorder.RaiseLevel(2f);
        Assert.Equal(1.0, rig.Controller.Level, 3);
    }

    [Fact]
    public async Task Level_IsResetWhenRecordingEnds()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        rig.Recorder.RaiseLevel(0.7f);

        rig.Controller.Cancel();

        Assert.Equal(0, rig.Controller.Level);
    }

    [Fact]
    public async Task LevelEventsAfterStop_AreIgnored()
    {
        await using var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        rig.Controller.Cancel();

        rig.Recorder.RaiseLevel(0.8f);

        Assert.Equal(0, rig.Controller.Level);
    }

    // ---- 清理 ----

    [Fact]
    public async Task Dispose_WhileRecording_ReleasesTheMicrophone()
    {
        var rig = new Rig();
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        Assert.True(rig.Recorder.IsRecording);

        await rig.DisposeAsync();

        Assert.False(rig.Recorder.IsRecording);
        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
    }

    [Fact]
    public async Task Dispose_WhileTranscribing_CancelsTheRequest()
    {
        var rig = new Rig();
        rig.Speech.Hang = true;
        await rig.Controller.StartAsync(VoiceOrigin.Workbench);
        var stop = rig.Controller.StopAsync(VoiceStopAction.Edit);

        await rig.DisposeAsync();
        await stop;

        Assert.Equal(VoiceInputState.Idle, rig.Controller.State);
        Assert.Equal(1, rig.Speech.CanceledCount);
    }

    [Fact]
    public async Task AfterDispose_NothingStartsAgain()
    {
        var rig = new Rig();
        await rig.DisposeAsync();

        await rig.Controller.StartAsync(VoiceOrigin.Workbench);

        Assert.Equal(0, rig.Recorder.StartCount);
    }

}
