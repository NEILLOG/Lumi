using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M6 6D：工作台的語音畫面——麥克風鈕、收音面板的三種狀態、Esc、焦點搬移、波形、語音提示與標籤，以及窄寬度的版面。</summary>
public class VoiceComposerTests
{
    private sealed class Rig
    {
        public FakeVoiceInput Voice { get; } = new();
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ChatComposer Composer { get; }

        public Rig(double width = 1200, double height = 760)
        {
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance, voice: Voice);
            Window = new WorkbenchWindow(Vm) { Width = width, Height = height };
            Window.Show();
            Window.UpdateLayout();
            Composer = Window.FindByName<ChatComposer>("Composer");
        }

        public async Task InitializeAsync()
        {
            await Vm.InitializeAsync();
            Window.UpdateLayout();
        }

        public T Named<T>(string name) where T : Control => Composer.FindByName<T>(name);

        public VoicePanel Panel => Named<VoicePanel>("VoicePanelHost");

        public void Refresh()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();   // 焦點搬移排在版面更新之後（見 ChatComposer）
            Window.UpdateLayout();
        }
    }

    private static Color TokenColor(Control host, string key)
    {
        Assert.True(host.TryFindResource(key, host.ActualThemeVariant, out var value), $"找不到 token {key}");
        return Assert.IsType<Color>(value);
    }

    private static Geometry Icon(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), $"找不到圖示資源 {key}");
        return Assert.IsAssignableFrom<Geometry>(value);
    }

    private static bool Inside(Control child, Control container)
    {
        var origin = child.TranslatePoint(default, container);
        return origin is { } p && p.X >= -0.5 && p.X + child.Bounds.Width <= container.Bounds.Width + 0.5;
    }

    // ---- 麥克風鈕 ----

    [AvaloniaFact]
    public async Task MicButton_IsEnabled_WhenVoiceCanStart_AndItsTipIsTheName()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var mic = rig.Named<Button>("MicButton");

        Assert.True(mic.IsEffectivelyEnabled);
        Assert.Same(rig.Vm.StartVoiceCommand, mic.Command);
        Assert.Equal(Strings.Composer_Mic, ToolTip.GetTip(mic));
        Assert.Equal(Strings.Composer_Mic, AutomationProperties.GetName(mic));
        Assert.Contains("tertiary", mic.Classes);
        Assert.Same(Icon("LumiIconMic"), mic.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task MicButton_WhenDisabled_ShowsTheReasonInItsTooltip()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var mic = rig.Named<Button>("MicButton");

        rig.Voice.SetAvailability(false, Strings.Voice_EndpointInvalid);
        rig.Refresh();

        Assert.False(mic.IsEffectivelyEnabled);
        Assert.Equal(Strings.Voice_EndpointInvalid, ToolTip.GetTip(mic));
        Assert.True(ToolTip.GetShowOnDisabled(mic));
        Assert.Equal(Strings.Voice_EndpointInvalid, rig.Vm.MicTip);

        rig.Voice.SetAvailability(true, null);
        rig.Refresh();
        Assert.True(mic.IsEffectivelyEnabled);
        rig.Window.Close();
    }

    // ---- 收音面板：錄音中 ----

    [AvaloniaFact]
    public async Task WhileRecording_TheVoicePanelReplacesTheInput_AndTheSendControlsAreHidden()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        Assert.False(rig.Named<TextBox>("InputBox").IsEffectivelyVisible);
        Assert.True(rig.Panel.IsEffectivelyVisible);
        Assert.True(rig.Panel.FindByName<Grid>("RecordingRow").IsEffectivelyVisible);
        Assert.False(rig.Panel.FindByName<Grid>("TranscribingRow").IsEffectivelyVisible);
        Assert.False(rig.Panel.FindByName<Border>("FailedRow").IsEffectivelyVisible);
        Assert.False(rig.Named<Button>("SendButton").IsEffectivelyVisible);
        Assert.False(rig.Named<TextBlock>("SendHint").IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileRecording_ThreeActions_HaveTheRightLevelsIconsTooltipsAndNames()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        var cancel = rig.Panel.FindByName<Button>("VoiceCancelButton");
        var edit = rig.Panel.FindByName<Button>("VoiceStopEditButton");
        var send = rig.Panel.FindByName<Button>("VoiceStopSendButton");

        Assert.Contains("tertiary", cancel.Classes);
        Assert.Contains("secondary", edit.Classes);
        Assert.Contains("primary", send.Classes);
        Assert.Equal(Strings.Voice_CancelTip, ToolTip.GetTip(cancel));
        Assert.Equal(Strings.Voice_StopEditTip, ToolTip.GetTip(edit));
        Assert.Equal(Strings.Voice_StopSendTip, ToolTip.GetTip(send));
        Assert.Equal(Strings.Voice_Cancel, AutomationProperties.GetName(cancel));
        Assert.Equal(Strings.Voice_StopEdit, AutomationProperties.GetName(edit));
        Assert.Equal(Strings.Voice_StopSend, AutomationProperties.GetName(send));
        // 停止並送出有圖示加文字（Wide）
        Assert.Contains(send.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Voice_StopSend);
        Assert.Same(Icon("LumiIconArrowUp"), send.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        Assert.Same(Icon("LumiIconX"), cancel.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        Assert.Same(rig.Vm.VoiceCancelCommand, cancel.Command);
        Assert.Same(rig.Vm.VoiceStopEditCommand, edit.Command);
        Assert.Same(rig.Vm.VoiceStopSendCommand, send.Command);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileRecording_TheRedDotUsesTheDangerDotToken_AndTheTimeIsShown()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Voice.SetElapsed(TimeSpan.FromSeconds(12));
        rig.Refresh();

        var dot = rig.Panel.GetVisualDescendants().OfType<Ellipse>().Single();
        Assert.Equal(TokenColor(dot, "LumiDangerDotColor"), Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
        Assert.Equal("00:12 / 01:30", rig.Panel.FindByName<Grid>("RecordingRow").GetVisualDescendants().OfType<TextBlock>().First(t => t.Text!.Contains('/')).Text);
        Assert.Equal("收音中 00:12", AutomationProperties.GetName(rig.Panel.FindByName<Border>("PanelRoot")));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileRecording_TheWaveform_ReceivesEveryLevel_AsThirtyTwoBars()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        var wave = rig.Panel.GetVisualDescendants().OfType<LevelWaveform>().Single();

        rig.Voice.SetLevel(0.6);
        rig.Refresh();

        Assert.Equal(LevelWaveform.BarCount, wave.Samples.Count);
        Assert.Equal(0.6, wave.Samples[^1], 3);
        rig.Voice.SetLevel(0.2);
        Assert.Equal(0.2, wave.Samples[^1], 3);
        Assert.Equal(0.6, wave.Samples[^2], 3);   // 舊的往左推
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_WhileRecording_CancelsTheRecording()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        rig.Panel.FindByName<Button>("VoiceStopEditButton").Focus();

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.Contains("cancel", rig.Voice.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_WhenNotRecording_DoesNothing()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Named<TextBox>("InputBox").Focus();

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.DoesNotContain("cancel", rig.Voice.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhileRecording_TheDefaultFocusIsStopAndEdit_NotSend()
    {
        var rig = new Rig();
        await rig.InitializeAsync();

        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        Assert.True(rig.Panel.FindByName<Button>("VoiceStopEditButton").IsFocused);
        rig.Window.Close();
    }

    // ---- 轉寫中 ----

    [AvaloniaFact]
    public async Task WhileTranscribing_ShowsASpinnerWithTheStatusAndACancel()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.RecordedDuration = TimeSpan.FromSeconds(12);
        rig.Voice.State = VoiceInputState.Transcribing;
        rig.Refresh();

        var row = rig.Panel.FindByName<Grid>("TranscribingRow");
        Assert.True(row.IsEffectivelyVisible);
        Assert.False(rig.Panel.FindByName<Grid>("RecordingRow").IsEffectivelyVisible);
        var spinner = row.GetVisualDescendants().OfType<LumiIcon>().First();   // 第一個是轉圈（第二個是取消鈕的 X）
        Assert.Contains("spin", spinner.Classes);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "正在辨識語音…（錄音長度 00:12）");
        var cancel = rig.Panel.FindByName<Button>("VoiceCancelTranscribeButton");
        Assert.True(cancel.IsEffectivelyVisible);
        Assert.Equal(Strings.Voice_CancelTranscribeTip, ToolTip.GetTip(cancel));
        Assert.Equal(Strings.Voice_Cancel, AutomationProperties.GetName(cancel));
        rig.Window.Close();
    }

    // ---- 失敗 ----

    [AvaloniaFact]
    public async Task WhenFailed_TheMessageIsShownWithIconAndText_AndRetryAndDiscardAreOffered()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.LastFailureMessage = "服務逾時，請稍後再試。";
        rig.Voice.State = VoiceInputState.Failed;
        rig.Refresh();

        var row = rig.Panel.FindByName<Border>("FailedRow");
        Assert.True(row.IsEffectivelyVisible);
        Assert.Equal(TokenColor(row, "LumiDangerBackgroundColor"), Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        Assert.Same(Icon("LumiIconAlertTriangle"), row.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "服務逾時，請稍後再試。");
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Voice_FailedTitle);

        var retry = rig.Panel.FindByName<Button>("VoiceRetryButton");
        var discard = rig.Panel.FindByName<Button>("VoiceDiscardButton");
        Assert.Contains("primary", retry.Classes);
        Assert.Contains("tertiary", discard.Classes);
        Assert.Equal(Strings.Voice_Retry, retry.Content);
        Assert.Equal(Strings.Voice_Discard, discard.Content);
        Assert.Equal(Strings.Voice_RetryTip, ToolTip.GetTip(retry));
        Assert.Equal(Strings.Voice_Retry, AutomationProperties.GetName(retry));
        Assert.Equal(Strings.Voice_Discard, AutomationProperties.GetName(discard));
        Assert.Same(rig.Vm.VoiceRetryCommand, retry.Command);
        Assert.Same(rig.Vm.VoiceDiscardCommand, discard.Command);
        Assert.True(retry.IsFocused);   // 焦點停在重試轉寫
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task WhenFailed_ThePanelKeepsTheInputAreaHeight()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.LastFailureMessage = "服務逾時";
        rig.Voice.State = VoiceInputState.Failed;
        rig.Refresh();

        Assert.True(rig.Panel.FindByName<Border>("PanelRoot").Bounds.Height >= 64 - 0.5);
        rig.Window.Close();
    }

    // ---- 焦點 ----

    [AvaloniaFact]
    public async Task FocusMovesIntoThePanel_WhenVoiceStarts_AndBackToTheInput_WhenItEnds()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Named<TextBox>("InputBox").Focus();

        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        Assert.True(rig.Panel.FindByName<Button>("VoiceStopEditButton").IsFocused);

        rig.Voice.State = VoiceInputState.Idle;
        rig.Refresh();
        Assert.True(rig.Named<TextBox>("InputBox").IsFocused);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task FocusMovesToRetry_WhenTranscriptionFails()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Transcribing;
        rig.Refresh();

        rig.Voice.LastFailureMessage = "失敗";
        rig.Voice.State = VoiceInputState.Failed;
        rig.Refresh();

        Assert.True(rig.Panel.FindByName<Button>("VoiceRetryButton").IsFocused);
        rig.Window.Close();
    }

    // ---- 版面：窄寬度與 Compact ----

    [AvaloniaTheory]
    [InlineData(800)]
    [InlineData(500)]
    public async Task InCompactAndNarrow_TheVoiceActionsCollapseToIconsWithinTheInputArea(double width)
    {
        var rig = new Rig(width, 650);
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        var area = rig.Named<Border>("InputArea");

        foreach (var name in new[] { "VoiceCancelButton", "VoiceStopEditButton", "VoiceStopSendButton" })
        {
            var button = rig.Panel.FindByName<Button>(name);
            Assert.True(button.Bounds.Width <= 32.5, $"{name} 寬度 {button.Bounds.Width}");
            Assert.False(button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("collapsible-label")).IsEffectivelyVisible);
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(button) as string));
            Assert.True(Inside(button, area), $"{name} 超出輸入區");
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task InWide_TheVoiceActionsShowTheirLabels_AndStayInsideTheInputArea()
    {
        var rig = new Rig(1200, 760);
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        var area = rig.Named<Border>("InputArea");

        foreach (var name in new[] { "VoiceCancelButton", "VoiceStopEditButton", "VoiceStopSendButton" })
        {
            var button = rig.Panel.FindByName<Button>(name);
            Assert.True(button.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("collapsible-label")).IsEffectivelyVisible);
            Assert.True(Inside(button, area), $"{name} 超出輸入區");
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheVoicePanel_IsAboutTheHeightOfTheInput_SoTheLayoutDoesNotJump()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var inputHeight = rig.Named<TextBox>("InputBox").Bounds.Height;

        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        Assert.True(Math.Abs(rig.Panel.FindByName<Border>("PanelRoot").Bounds.Height - inputHeight) <= 16, $"面板 {rig.Panel.FindByName<Border>("PanelRoot").Bounds.Height}，輸入框 {inputHeight}");
        rig.Window.Close();
    }

    // ---- 提示與標籤 ----

    [AvaloniaFact]
    public async Task VoiceNotice_ShowsAboveTheInput_AndGoesAwayWithTheNextInput()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var notice = rig.Named<TextBlock>("VoiceNoticeText");
        Assert.False(notice.IsEffectivelyVisible);

        rig.Voice.Notice = Strings.Voice_NoSpeech;
        rig.Refresh();
        Assert.True(notice.IsEffectivelyVisible);
        Assert.Equal(Strings.Voice_NoSpeech, notice.Text);
        Assert.Equal(Strings.Voice_NoSpeech, rig.Vm.VoiceNotice);

        rig.Vm.InputText = "x";
        rig.Refresh();
        Assert.False(notice.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheDraftLabel_IsAChip_AboveTheInput_WhileTheTextCameFromVoice()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var chip = rig.Named<Border>("VoiceDraftChip");
        Assert.False(chip.IsEffectivelyVisible);

        rig.Vm.InputText = "";
        rig.Voice.State = VoiceInputState.Idle;
        rig.Voice.RaiseTranscribed(new VoiceTranscribedArgs("語音文字", VoiceStopAction.Edit, null, null, VoiceOrigin.Workbench));
        rig.Refresh();

        Assert.True(chip.IsEffectivelyVisible);
        Assert.Contains("chip", chip.Classes);
        Assert.Contains(chip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Voice_ChipLabel);
        Assert.Equal("語音文字", rig.Vm.InputText);
        Assert.True(rig.Named<TextBox>("InputBox").IsFocused);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task AddButtons_AreDisabledWhileVoiceIsActive_WithTheVoiceReason()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var file = rig.Named<Button>("AddFileButton");
        var folder = rig.Named<Button>("AddFolderButton");
        Assert.True(file.IsEffectivelyEnabled);

        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        foreach (var button in new[] { file, folder })
        {
            Assert.False(button.IsEffectivelyEnabled);
            Assert.Equal(Strings.Composer_VoiceBlocked, ToolTip.GetTip(button));
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task AfterAVoiceRound_TheSendButtonReturns()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        rig.Voice.State = VoiceInputState.Idle;
        rig.Refresh();

        Assert.True(rig.Named<Button>("SendButton").IsEffectivelyVisible);
        Assert.True(rig.Named<TextBlock>("SendHint").IsEffectivelyVisible);
        Assert.True(rig.Named<TextBox>("InputBox").IsEffectivelyVisible);
        Assert.False(rig.Panel.IsEffectivelyVisible);
        rig.Window.Close();
    }
}
