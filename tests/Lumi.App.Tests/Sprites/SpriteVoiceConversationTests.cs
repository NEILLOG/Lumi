using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Speech;
using Lumi.Core.Tools;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Sprites;

/// <summary>
/// M6 6E：小精靈的語音流程（真的 <see cref="ChatSession"/>、真的 <see cref="SpriteWindow"/>、假的供應商；語音用假的控制器，
/// 最後一組用真的 <see cref="VoiceInputController"/> 配假的錄音器與假的 Whisper）。
/// </summary>
public class SpriteVoiceConversationTests
{
    private static readonly SpeechSettings Configured = new("https://speech.example.com/v1", "zh", 120, "abcd", "speech.example.com");

    private sealed class NoNativeOverlay : Lumi.Core.Platform.IOverlayPlatform
    {
        public string Name => "none";
        public bool IsSupported => false;
        public bool ClickThroughActive => false;
        public void Attach(nint nativeWindowHandle) { }
        public bool TryGetCursorInWindow(double renderScaling, double clientHeightDip, out Lumi.Core.Platform.OverlayPoint dip) { dip = default; return false; }
        public void SetClickThrough(bool on) { }
    }

    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public FakeModelCatalog Catalog { get; } = new();
        public FakeAudioRecorder Recorder { get; } = new();
        public FakeSpeechToText Speech { get; } = new();
        public FakeVoiceInput FakeVoice { get; } = new();
        public IVoiceInputController Voice { get; }
        public SequenceChatProvider Provider { get; }
        public ChatSession Session { get; }
        public SpriteWindow Window { get; }
        public SpriteBubbleViewModel Bubble => Window.BubbleViewModel;
        public List<string?> QuotesAtRunStart { get; } = [];

        public Rig(SequenceChatProvider provider, bool realVoice = false)
        {
            Provider = provider;
            var time = new FakeTimeProvider();
            var approvals = new ToolApprovalService();
            var service = new ConversationService(provider, Store, Catalog, new ToolRegistry([]), approvals, new ToolPermissionMemory(),
                new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance);
            Session = new ChatSession(service, Store);

            if (realVoice)
            {
                Catalog.Current = Catalog.Current with { Speech = Configured };
                Recorder.StopResult = new RecordedAudio(WavFormat.Silence(TimeSpan.FromSeconds(2)), TimeSpan.FromSeconds(2), false, false);
                Voice = new VoiceInputController(Recorder, Speech, Catalog, Session, new SyncUiDispatcher(), time,
                    NullLogger<VoiceInputController>.Instance);
            }
            else
            {
                Voice = FakeVoice;
            }

            var options = new SpriteShellOptions();
            var mapper = new SpriteStateMapper(options, time);
            var bubble = new SpriteBubbleViewModel(options, time, approvals);
            var modes = new AppModeCoordinator(Session, new FakeWindowHost(), Catalog, options, time, NullLogger<AppModeCoordinator>.Instance);
            Window = new SpriteWindow(new SpriteCatalog([new OrbSpriteFactory()]), new NoNativeOverlay(), Catalog, options, time,
                NullLogger<SpriteWindow>.Instance, null, mapper, bubble, modes, Session, new FakeSettingsWindowService(), Voice);
            new SpriteConversationController(Session, mapper, bubble, approvals, Window, modes, new FakeApiKeyPrompt(false),
                new FakeSettingsWindowService(), NullLogger<SpriteConversationController>.Instance, Voice).Attach();
            Session.RunStarted += (_, _) => QuotesAtRunStart.Add(Bubble.QuoteText);   // 控制器先訂閱：這時氣泡已經有引用
            Window.Show();
            Window.UpdateLayout();
        }

        public async Task StartAsync()
        {
            await Session.InitializeAsync("新對話");
            Session.TrySetMode(ChatMode.Sprite);
        }

        public string? ConversationId => Session.ConversationId;

        public void Transcribed(string text, VoiceStopAction action, string? conversationId = null, VoiceOrigin origin = VoiceOrigin.Sprite) =>
            FakeVoice.RaiseTranscribed(new VoiceTranscribedArgs(text, action, conversationId ?? Session.ConversationId, "zh", origin));

        public TextBox Input => Window.FindControl<TextBox>("Input")!;

        public Border Chip => Window.FindControl<Border>("VoiceChip")!;

        public Button BubbleButton(string name) => Window.FindControl<SpriteBubbleView>("Bubble")!.FindControl<Button>(name)!;
    }

    private static SequenceChatProvider Answer(string text) => new(SequenceChatProvider.Answer(text));

    private static string? TextOf(ChatMessage message) => string.Concat(message.Content.OfType<Lumi.Core.Chat.TextBlock>().Select(t => t.Text));

    // ---- 送出與編輯 ----

    [AvaloniaFact]
    public async Task StopAndSend_QuotesTheTranscript_ThenSendsAndAnswers()
    {
        var rig = new Rig(Answer("明天會下雨。"));
        await rig.StartAsync();

        rig.Transcribed("明天台中會下雨嗎？", VoiceStopAction.Send);
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding && rig.Bubble.Kind == SpriteBubbleKind.Done);

        Assert.Equal(["明天台中會下雨嗎？"], rig.QuotesAtRunStart);   // 進入思考前，引用已經在氣泡上
        Assert.Equal("明天會下雨。", rig.Bubble.Text);
        var messages = await rig.Store.GetMessagesAsync(rig.ConversationId!);
        Assert.Equal(2, messages.Count);
        Assert.Equal("明天台中會下雨嗎？", TextOf(messages[0]));
        Assert.True(string.IsNullOrEmpty(rig.Input.Text));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task StopAndEdit_PutsTheTextInTheInput_WithTheVoiceLabel_AndSendsNothing()
    {
        var rig = new Rig(Answer("不該出現"));
        await rig.StartAsync();

        rig.Transcribed("明天台中會下雨嗎？", VoiceStopAction.Edit);

        Assert.Equal("明天台中會下雨嗎？", rig.Input.Text);
        Assert.True(rig.Chip.IsVisible);
        Assert.Equal(0, rig.Provider.Calls);
        Assert.Empty(await rig.Store.GetMessagesAsync(rig.ConversationId!));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheVoiceLabel_GoesAway_WhenTheUserEditsTheText()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        rig.Transcribed("明天會下雨", VoiceStopAction.Edit);
        Assert.True(rig.Chip.IsVisible);

        rig.Input.Text = "明天會下雨嗎";

        Assert.False(rig.Chip.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task AnEdit_AppendsToAnExistingDraft_WithASpace()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        rig.Input.Text = "請問";

        rig.Transcribed("明天會下雨嗎", VoiceStopAction.Edit);

        Assert.Equal("請問 明天會下雨嗎", rig.Input.Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task StopAndSend_WithADraftInTheInput_IsNotSent_AndSaysSo()
    {
        var rig = new Rig(Answer("不該出現"));
        await rig.StartAsync();
        rig.Input.Text = "草稿";

        rig.Transcribed("語音文字", VoiceStopAction.Send);

        Assert.Equal("草稿 語音文字", rig.Input.Text);
        Assert.Equal(SpriteBubbleKind.Notice, rig.Bubble.Kind);
        Assert.Equal(Strings.Voice_NoticeHasDraft, rig.Bubble.NoticeText);
        Assert.Equal(0, rig.Provider.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task StopAndSend_ForAnotherConversation_IsEditedWithANotice()
    {
        var rig = new Rig(Answer("不該出現"));
        await rig.StartAsync();

        rig.Transcribed("語音文字", VoiceStopAction.Send, conversationId: "另一則對話");

        Assert.Equal("語音文字", rig.Input.Text);
        Assert.Equal(Strings.Voice_NoticeEditOnly, rig.Bubble.NoticeText);
        Assert.Equal(0, rig.Provider.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task StopAndSend_WhileResponding_IsEditedWithANotice()
    {
        var provider = new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 };
        var rig = new Rig(provider);
        await rig.StartAsync();
        rig.Window.FindControl<TextBox>("Input")!.Text = "一";
        rig.Window.FindControl<Button>("SendButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await SettingsHost.WaitUntil(() => rig.Session.IsResponding);

        rig.Transcribed("語音文字", VoiceStopAction.Send);

        Assert.Equal("語音文字", rig.Input.Text);
        Assert.Equal(Strings.Voice_NoticeEditOnly, rig.Bubble.NoticeText);
        Assert.Equal(1, provider.Calls);
        rig.Session.Stop();
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task StopAndSend_WhileTheSpriteIsHidden_IsEdited()
    {
        var rig = new Rig(Answer("不該出現"));
        await rig.StartAsync();
        rig.Window.Hide();

        rig.Transcribed("語音文字", VoiceStopAction.Send);

        Assert.Equal("語音文字", rig.Input.Text);
        Assert.Equal(0, rig.Provider.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ResultsOfTheWorkbench_AreIgnoredBySprite()
    {
        var rig = new Rig(Answer("不該出現"));
        await rig.StartAsync();

        rig.Transcribed("工作台的語音", VoiceStopAction.Send, origin: VoiceOrigin.Workbench);

        Assert.True(string.IsNullOrEmpty(rig.Input.Text));
        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(0, rig.Provider.Calls);
        rig.Window.Close();
    }

    // ---- 狀態與提示 ----

    [AvaloniaFact]
    public async Task TheCharacter_FollowsTheVoiceState_AndTheInputRowGivesWayToTheVoiceBar()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        var inputRow = rig.Window.FindControl<Grid>("InputRow")!;
        var voiceBar = rig.Window.FindControl<Control>("VoiceBar")!;
        rig.FakeVoice.Origin = VoiceOrigin.Sprite;

        rig.FakeVoice.State = VoiceInputState.Recording;
        rig.Window.UpdateLayout();
        Assert.Equal(SpriteState.Listening, rig.Window.AppliedState);
        Assert.False(inputRow.IsVisible);
        Assert.True(voiceBar.IsVisible);

        rig.FakeVoice.State = VoiceInputState.Transcribing;
        Assert.Equal(SpriteState.Transcribing, rig.Window.AppliedState);

        rig.FakeVoice.State = VoiceInputState.Idle;
        rig.Window.UpdateLayout();
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);
        Assert.True(inputRow.IsVisible);
        Assert.False(voiceBar.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ANotice_FromTheSprite_ShowsAsANoticeBubble()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        rig.FakeVoice.Origin = VoiceOrigin.Sprite;

        rig.FakeVoice.Notice = Strings.Voice_TooShort;

        Assert.Equal(SpriteBubbleKind.Notice, rig.Bubble.Kind);
        Assert.Equal(Strings.Voice_TooShort, rig.Bubble.NoticeText);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ANotice_FromTheWorkbench_DoesNotShowInTheSprite()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        rig.FakeVoice.Origin = VoiceOrigin.Workbench;

        rig.FakeVoice.Notice = Strings.Voice_TooShort;

        Assert.False(rig.Bubble.IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task AFailure_ShowsAnErrorBubble_WithRetryAndDiscard_AndClearsWhenHandled()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        rig.FakeVoice.Origin = VoiceOrigin.Sprite;
        rig.FakeVoice.LastFailureMessage = "服務暫時無法使用";

        rig.FakeVoice.State = VoiceInputState.Failed;

        Assert.Equal(SpriteBubbleKind.Error, rig.Bubble.Kind);
        Assert.True(rig.Bubble.IsVoiceFailure);
        Assert.Equal(SpriteVoiceBarViewModel.FailureSentence("服務暫時無法使用"), rig.Bubble.ErrorText);
        Assert.Equal(SpriteState.Error, rig.Window.AppliedState);

        rig.BubbleButton("VoiceRetryButton").Command!.Execute(null);
        Assert.Contains("retry", rig.FakeVoice.Calls);

        rig.FakeVoice.State = VoiceInputState.Transcribing;   // 重試開始：失敗氣泡收起，角色回到轉寫中
        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(SpriteState.Transcribing, rig.Window.AppliedState);

        rig.FakeVoice.State = VoiceInputState.Failed;
        rig.BubbleButton("VoiceDiscardButton").Command!.Execute(null);
        Assert.Contains("discard", rig.FakeVoice.Calls);
        rig.FakeVoice.State = VoiceInputState.Idle;
        Assert.False(rig.Bubble.IsVisible);
        Assert.Equal(SpriteState.Idle, rig.Window.AppliedState);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task AFailure_FromTheWorkbench_ShowsNoSpriteBubble()
    {
        var rig = new Rig(Answer("好"));
        await rig.StartAsync();
        rig.FakeVoice.Origin = VoiceOrigin.Workbench;

        rig.FakeVoice.State = VoiceInputState.Failed;

        Assert.False(rig.Bubble.IsVisible);
        rig.Window.Close();
    }

    // ---- 真的語音控制器（假的錄音器、假的 Whisper） ----

    [AvaloniaFact]
    public async Task WithTheRealController_StopAndSend_QuotesThenAnswers()
    {
        var rig = new Rig(Answer("明天會下雨。"), realVoice: true);
        await rig.StartAsync();
        rig.Speech.Result = new TranscriptionResult("明天台中會下雨嗎？", "zh", TimeSpan.FromSeconds(2));

        await rig.Voice.StartAsync(VoiceOrigin.Sprite);
        Assert.Equal(VoiceInputState.Recording, rig.Voice.State);
        await rig.Voice.StopAsync(VoiceStopAction.Send);
        await SettingsHost.WaitUntil(() => !rig.Session.IsResponding && rig.Bubble.Kind == SpriteBubbleKind.Done);

        Assert.Equal(["明天台中會下雨嗎？"], rig.QuotesAtRunStart);
        Assert.Equal("明天會下雨。", rig.Bubble.Text);
        Assert.Equal(VoiceInputState.Idle, rig.Voice.State);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WithTheRealController_AFailedTranscription_CanBeRetried_IntoTheInput()
    {
        var rig = new Rig(Answer("不該出現"), realVoice: true);
        await rig.StartAsync();
        rig.Speech.Failures.Enqueue(new SpeechFailure(SpeechFailureKind.ServerError, "服務暫時無法使用", true));

        await rig.Voice.StartAsync(VoiceOrigin.Sprite);
        await rig.Voice.StopAsync(VoiceStopAction.Edit);
        Assert.Equal(VoiceInputState.Failed, rig.Voice.State);
        Assert.True(rig.Bubble.IsVoiceFailure);
        Assert.Equal(SpriteState.Error, rig.Window.AppliedState);

        rig.Speech.Result = new TranscriptionResult("重試成功", "zh", TimeSpan.FromSeconds(2));
        rig.BubbleButton("VoiceRetryButton").Command!.Execute(null);
        await SettingsHost.WaitUntil(() => rig.Voice.State == VoiceInputState.Idle);

        Assert.Equal("重試成功", rig.Input.Text);
        Assert.Equal(0, rig.Provider.Calls);
        Assert.False(rig.Bubble.IsVisible);
        rig.Window.Close();
    }
}
