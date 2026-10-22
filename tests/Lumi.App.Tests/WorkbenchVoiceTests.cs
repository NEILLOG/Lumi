using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Speech;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests;

/// <summary>M6 6D：工作台的語音輸入——轉寫結果放進輸入框、「語音轉寫」標籤、停止並送出、收音狀態的文字與旗標。</summary>
public class WorkbenchVoiceTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public FakeModelCatalog Catalog { get; } = new();
        public SequenceChatProvider Provider { get; }
        public ChatSession Session { get; }
        public FakeVoiceInput Voice { get; } = new();
        public WorkbenchViewModel Vm { get; }
        public List<string> Notices { get; } = [];

        public Rig()
        {
            Provider = new SequenceChatProvider(SequenceChatProvider.Answer("好"));
            var service = new ConversationService(Provider, Store, Catalog);
            Session = new ChatSession(service, Store, NullLogger<ChatSession>.Instance);
            Vm = new WorkbenchViewModel(service, Store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, session: Session, voice: Voice);
            Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkbenchViewModel.VoiceNotice)) Notices.Add(Vm.VoiceNotice ?? ""); };
        }

        public Task InitializeAsync() => Vm.InitializeAsync();

        /// <summary>模擬語音轉寫完成：狀態先回到閒置（和真的控制器一樣），再發出結果。</summary>
        public void Transcribed(string text, VoiceStopAction action, string? conversationId = null)
        {
            Voice.State = VoiceInputState.Idle;
            Voice.RaiseTranscribed(new VoiceTranscribedArgs(text, action, conversationId ?? Session.ConversationId, "zh", VoiceOrigin.Workbench));
        }
    }

    // ---- 麥克風鈕 ----

    [Fact]
    public async Task MicCommand_FollowsTheController_AndItsReasonIsTheTip()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        Assert.True(rig.Vm.StartVoiceCommand.CanExecute(null));
        Assert.Equal(Strings.Composer_Mic, rig.Vm.MicTip);
        Assert.True(rig.Vm.VoiceCanStart);

        rig.Voice.SetAvailability(false, Strings.Voice_TokenMissing);

        Assert.False(rig.Vm.StartVoiceCommand.CanExecute(null));
        Assert.Equal(Strings.Voice_TokenMissing, rig.Vm.MicTip);
        Assert.False(rig.Vm.VoiceCanStart);
    }

    [Fact]
    public async Task MicCommand_CallsTheController()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        await rig.Vm.StartVoiceCommand.ExecuteAsync(null);

        Assert.Equal(["start"], rig.Voice.Calls);
    }

    [Fact]
    public async Task WhileTheSpriteIsRecording_TheWorkbenchMicSaysAnotherWindowIsUsingVoice()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Voice.SetAvailability(false, Strings.Voice_BusyReason);   // 控制器：忙碌中不能開始

        Assert.False(rig.Vm.VoiceCanStart);
        Assert.Equal(Strings.Voice_OtherWindow, rig.Vm.MicTip);
    }

    [Fact]
    public async Task WhileTheWorkbenchIsRecording_TheMicTipIsTheBusyReason()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.Origin = VoiceOrigin.Workbench;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Voice.SetAvailability(false, Strings.Voice_BusyReason);

        Assert.Equal(Strings.Voice_BusyReason, rig.Vm.MicTip);
    }

    [Fact]
    public async Task ASpriteTranscription_IsNotPutInTheWorkbenchInput()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        rig.Voice.RaiseTranscribed(new VoiceTranscribedArgs("小精靈的語音", VoiceStopAction.Edit, rig.Session.ConversationId, "zh", VoiceOrigin.Sprite));

        Assert.Equal("", rig.Vm.InputText);
        Assert.False(rig.Vm.IsVoiceDraft);
        Assert.Equal(0, rig.Provider.Calls);
    }

    [Fact]
    public async Task ASpriteNotice_IsNotShownInTheWorkbench()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.Origin = VoiceOrigin.Sprite;

        rig.Voice.Notice = Strings.Voice_TooShort;

        Assert.Null(rig.Vm.VoiceNotice);
    }

    [Fact]
    public async Task RespondingOrViewOnly_TheMicIsOff_BecauseTheControllerSaysSo()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.SetAvailability(false, Strings.Voice_RespondingBlocked);
        Assert.Equal(Strings.Voice_RespondingBlocked, rig.Vm.MicTip);
    }

    // ---- 收音狀態的旗標與文字 ----

    [Fact]
    public async Task TheStateFlags_FollowTheController()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        Assert.False(rig.Vm.IsVoiceActive);

        rig.Voice.State = VoiceInputState.Recording;
        Assert.True(rig.Vm.IsVoiceActive);
        Assert.True(rig.Vm.IsVoiceRecording);
        Assert.False(rig.Vm.IsVoiceTranscribing);

        rig.Voice.State = VoiceInputState.Transcribing;
        Assert.True(rig.Vm.IsVoiceActive);
        Assert.True(rig.Vm.IsVoiceTranscribing);
        Assert.False(rig.Vm.IsVoiceRecording);

        rig.Voice.State = VoiceInputState.Failed;
        Assert.True(rig.Vm.IsVoiceActive);
        Assert.True(rig.Vm.IsVoiceFailed);
    }

    [Fact]
    public async Task TheTimeAndStatusText_AreFormattedForTheUser()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        rig.Voice.State = VoiceInputState.Recording;
        rig.Voice.Elapsed = TimeSpan.FromSeconds(65);
        Assert.Equal("01:05 / 01:30", rig.Vm.VoiceTimeText);
        Assert.Equal("收音中 01:05", rig.Vm.VoiceStatusText);

        rig.Voice.State = VoiceInputState.Transcribing;
        rig.Voice.RecordedDuration = TimeSpan.FromSeconds(12);
        Assert.Equal("正在辨識語音…（錄音長度 00:12）", rig.Vm.VoiceStatusText);

        rig.Voice.State = VoiceInputState.Failed;
        rig.Voice.LastFailureMessage = "服務逾時";
        Assert.Equal(Strings.Voice_FailedTitle, rig.Vm.VoiceStatusText);
        Assert.Equal("服務逾時", rig.Vm.VoiceFailureText);
    }

    [Fact]
    public async Task WhileVoiceIsActive_SendIsOff_AndTheAddButtonsExplainWhy()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Vm.InputText = "有文字";
        Assert.True(rig.Vm.SendCommand.CanExecute(null));
        Assert.True(rig.Vm.ShowSendButton);

        rig.Voice.State = VoiceInputState.Recording;

        Assert.False(rig.Vm.SendCommand.CanExecute(null));
        Assert.False(rig.Vm.ShowSendButton);
        Assert.False(rig.Vm.ShowSendHint);
        Assert.True(rig.Vm.Context.IsLocked);
        Assert.Equal(Strings.Composer_VoiceBlocked, rig.Vm.AddFileTip);
        Assert.Equal(Strings.Composer_VoiceBlocked, rig.Vm.AddFolderTip);

        rig.Voice.State = VoiceInputState.Idle;
        Assert.True(rig.Vm.SendCommand.CanExecute(null));
        Assert.False(rig.Vm.Context.IsLocked);
        Assert.Equal(Strings.Context_AddFile, rig.Vm.AddFileTip);
    }

    [Fact]
    public async Task TheVoiceCommands_CallTheController()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        await rig.Vm.VoiceStopEditCommand.ExecuteAsync(null);
        await rig.Vm.VoiceStopSendCommand.ExecuteAsync(null);
        rig.Vm.VoiceCancelCommand.Execute(null);
        await rig.Vm.VoiceRetryCommand.ExecuteAsync(null);
        rig.Vm.VoiceDiscardCommand.Execute(null);

        Assert.Equal(["stop:Edit", "stop:Send", "cancel", "retry", "discard"], rig.Voice.Calls);
    }

    [Fact]
    public async Task ControllerNotice_IsMirroredToTheWorkbench()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        rig.Voice.Notice = Strings.Voice_TooShort;

        Assert.True(rig.Vm.HasVoiceNotice);
        Assert.Equal(Strings.Voice_TooShort, rig.Vm.VoiceNotice);
    }

    // ---- 轉寫結果：編輯 ----

    [Fact]
    public async Task Transcribed_Edit_PutsTheTextIntoTheInput_AndMarksTheDraft()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var focusRequests = 0;
        rig.Vm.FocusInputRequested += (_, _) => focusRequests++;

        rig.Transcribed("你好嗎", VoiceStopAction.Edit);

        Assert.Equal("你好嗎", rig.Vm.InputText);
        Assert.True(rig.Vm.IsVoiceDraft);
        Assert.False(rig.Vm.HasVoiceNotice);
        Assert.Equal(1, focusRequests);
        Assert.DoesNotContain(rig.Vm.Messages, m => m.IsUser);   // 不送出
    }

    [Fact]
    public async Task Transcribed_Edit_AppendsToAnExistingDraft_WithANewline()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Vm.InputText = "先寫一句";

        rig.Transcribed("接著說這句", VoiceStopAction.Edit);

        Assert.Equal("先寫一句\n接著說這句", rig.Vm.InputText);
        Assert.True(rig.Vm.IsVoiceDraft);
    }

    [Fact]
    public async Task Transcribed_Edit_WithAWhitespaceOnlyDraft_ReplacesIt()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Vm.InputText = "   ";

        rig.Transcribed("你好", VoiceStopAction.Edit);

        Assert.Equal("你好", rig.Vm.InputText);
    }

    [Fact]
    public async Task TheDraftLabel_GoesAway_WhenTheUserChangesOrClearsTheText()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Transcribed("你好", VoiceStopAction.Edit);
        Assert.True(rig.Vm.IsVoiceDraft);

        rig.Vm.InputText = "你好嗎";   // 改動
        Assert.False(rig.Vm.IsVoiceDraft);

        rig.Transcribed("再見", VoiceStopAction.Edit);
        Assert.True(rig.Vm.IsVoiceDraft);
        rig.Vm.InputText = "";        // 清空
        Assert.False(rig.Vm.IsVoiceDraft);
    }

    [Fact]
    public async Task TheDraftLabel_StaysWhileTheTextIsUnchanged_AndAddedVoiceTextKeepsIt()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Transcribed("第一句", VoiceStopAction.Edit);

        rig.Transcribed("第二句", VoiceStopAction.Edit);

        Assert.True(rig.Vm.IsVoiceDraft);
        Assert.Equal("第一句\n第二句", rig.Vm.InputText);
    }

    [Fact]
    public async Task VoiceNotice_IsClearedByTheNextInput()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.Notice = Strings.Voice_TooShort;
        Assert.True(rig.Vm.HasVoiceNotice);

        rig.Vm.InputText = "x";

        Assert.False(rig.Vm.HasVoiceNotice);
    }

    // ---- 轉寫結果：停止並送出 ----

    [Fact]
    public async Task Transcribed_Send_WhenTheConversationMatchesAndTheInputIsEmpty_SendsThroughTheNormalPath()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        rig.Transcribed("請介紹你自己", VoiceStopAction.Send);
        await rig.Vm.SendCommand.ExecutionTask!;

        Assert.Contains(rig.Vm.Messages, m => m.IsUser && m.Text == "請介紹你自己");
        Assert.Equal("", rig.Vm.InputText);
        Assert.False(rig.Vm.IsVoiceDraft);
        Assert.False(rig.Vm.HasVoiceNotice);
    }

    [Fact]
    public async Task Transcribed_Send_WhenTheConversationChanged_FallsBackToEdit_WithANotice()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        rig.Transcribed("要送的話", VoiceStopAction.Send, conversationId: "some-other-conversation");

        Assert.DoesNotContain(rig.Vm.Messages, m => m.IsUser);
        Assert.Equal("要送的話", rig.Vm.InputText);
        Assert.True(rig.Vm.IsVoiceDraft);
        Assert.Equal(Strings.Voice_NoticeEditOnly, rig.Vm.VoiceNotice);
    }

    [Fact]
    public async Task Transcribed_Send_WhileResponding_FallsBackToEdit_WithANotice()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Vm.IsResponding = true;

        rig.Transcribed("回應中說的話", VoiceStopAction.Send);

        Assert.Equal("回應中說的話", rig.Vm.InputText);
        Assert.Equal(Strings.Voice_NoticeEditOnly, rig.Vm.VoiceNotice);
        Assert.DoesNotContain(rig.Vm.Messages, m => m.IsUser);
        rig.Vm.IsResponding = false;
    }

    [Fact]
    public async Task Transcribed_Send_WithADraftAlreadyInTheInput_DoesNotSendTheDraft_AndAppends()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Vm.InputText = "草稿";

        rig.Transcribed("語音", VoiceStopAction.Send);

        Assert.DoesNotContain(rig.Vm.Messages, m => m.IsUser);
        Assert.Equal("草稿\n語音", rig.Vm.InputText);
        Assert.Equal(Strings.Voice_NoticeHasDraft, rig.Vm.VoiceNotice);
    }

    [Fact]
    public async Task Transcribed_Edit_WhileResponding_IsStillAnEdit_WithANotice()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Vm.IsResponding = true;

        rig.Transcribed("回應中的語音", VoiceStopAction.Edit);

        Assert.Equal("回應中的語音", rig.Vm.InputText);
        Assert.Equal(Strings.Voice_NoticeEditOnly, rig.Vm.VoiceNotice);
        rig.Vm.IsResponding = false;
    }
}
