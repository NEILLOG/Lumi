using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Orb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>M6 6E：小精靈輸入列的語音畫面——麥克風鈕（停用原因、Tooltip、無障礙名稱）、收音列的三種狀態（按鈕尺寸、名稱、Tooltip、都在輸入列框內）、Esc、語音標籤。</summary>
public class SpriteVoiceBarTests
{
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
        public FakeVoiceInput Voice { get; } = new();
        public SpriteWindow Window { get; }

        public Rig(bool shown = true)
        {
            Window = new SpriteWindow(new SpriteCatalog([new OrbSpriteFactory()]), new NoNativeOverlay(), new FakeModelCatalog(),
                new SpriteShellOptions(), new FakeTimeProvider(), NullLogger<SpriteWindow>.Instance, voice: Voice);
            if (shown)
                Window.Show();
            Window.UpdateLayout();
        }

        public T Named<T>(string name) where T : Control => Window.FindControl<T>(name)!;

        /// <summary>收音列裡的控制項（它在 <see cref="SpriteVoiceBar"/> 自己的名稱範圍內）。</summary>
        public T In<T>(string name) where T : Control => Named<SpriteVoiceBar>("VoiceBar").FindControl<T>(name)!;

        public Border Bar => Named<Border>("InputBar");

        public void Refresh()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();   // 焦點搬移排在版面更新之後（見 SpriteWindow.OnVoiceStateMaybeChanged）
            Window.UpdateLayout();
        }

        public bool InsideBar(Control child)
        {
            var origin = child.TranslatePoint(default, Bar);
            return origin is { } o && o.X >= 0 && o.Y >= 0
                && o.X + child.Bounds.Width <= Bar.Bounds.Width + 0.001 && o.Y + child.Bounds.Height <= Bar.Bounds.Height + 0.001;
        }
    }

    // ---- 麥克風鈕 ----

    [AvaloniaFact]
    public void TheMicrophone_IsEnabled_WithAnAccessibleNameAndTooltip()
    {
        var rig = new Rig();
        var mic = rig.Named<Button>("MicButton");

        Assert.True(mic.IsEnabled);
        Assert.Equal(Strings.Composer_Mic, AutomationProperties.GetName(mic));
        Assert.Equal(Strings.Composer_Mic, ToolTip.GetTip(mic));
        Assert.Equal(new Size(36, 36), mic.Bounds.Size);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheMicrophone_IsDisabled_WithTheReasonInItsTooltip_ShowingOnDisabled()
    {
        var rig = new Rig();
        rig.Voice.SetAvailability(false, Strings.Voice_NotConfigured);
        rig.Refresh();

        var mic = rig.Named<Button>("MicButton");
        Assert.False(mic.IsEnabled);
        Assert.Equal(Strings.Voice_NotConfigured, ToolTip.GetTip(mic));
        Assert.True(ToolTip.GetShowOnDisabled(mic));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheMicrophone_IsDisabled_WhileTheSpriteIsHidden()
    {
        var rig = new Rig(shown: false);
        rig.Refresh();

        Assert.False(rig.Named<Button>("MicButton").IsEnabled);
        Assert.Equal(Strings.Sprite_VoiceHidden, ToolTip.GetTip(rig.Named<Button>("MicButton")));
    }

    [AvaloniaFact]
    public void TheMicrophone_IsDisabled_WithTheOtherWindowReason_WhenTheWorkbenchIsRecording()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Workbench;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        var mic = rig.Named<Button>("MicButton");
        Assert.False(mic.IsEnabled);
        Assert.Equal(Strings.Voice_OtherWindow, ToolTip.GetTip(mic));
        rig.Window.Close();
    }

    // ---- 收音列 ----

    [AvaloniaFact]
    public void Recording_ReplacesTheInputRow_WithThreeNamedIconButtons_InsideTheBar()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        Assert.False(rig.Named<Grid>("InputRow").IsVisible);
        Assert.True(rig.In<Grid>("RecordingRow").IsVisible);
        Assert.False(rig.In<Grid>("TranscribingRow").IsVisible);
        Assert.False(rig.In<Grid>("FailedRow").IsVisible);

        var cancel = rig.In<Button>("CancelRecordingButton");
        var stopEdit = rig.In<Button>("StopEditButton");
        var stopSend = rig.In<Button>("StopSendButton");
        Assert.Equal(Strings.Voice_Cancel, AutomationProperties.GetName(cancel));
        Assert.Equal(Strings.Voice_CancelTip, ToolTip.GetTip(cancel));
        Assert.Equal(Strings.Voice_StopEdit, AutomationProperties.GetName(stopEdit));
        Assert.Equal(Strings.Voice_StopEditTip, ToolTip.GetTip(stopEdit));
        Assert.Equal(Strings.Voice_StopSend, AutomationProperties.GetName(stopSend));
        Assert.Equal(Strings.Voice_StopSendTip, ToolTip.GetTip(stopSend));

        foreach (var button in new[] { cancel, stopEdit, stopSend })
        {
            Assert.Equal(new Size(36, 36), button.Bounds.Size);
            Assert.True(rig.InsideBar(button), button.Name);
        }
        Assert.True(rig.InsideBar(rig.Window.GetVisualDescendants().OfType<LevelWaveform>().Single()));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Recording_TheRowNameCarriesTheClock()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Voice.SetElapsed(TimeSpan.FromSeconds(12));
        rig.Refresh();

        Assert.Equal("收音中 00:12", AutomationProperties.GetName(rig.In<Grid>("RecordingRow")));
        Assert.Contains(rig.Window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "00:12");
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Transcribing_ShowsTheStatus_AndACancelButton()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Transcribing;
        rig.Refresh();

        Assert.True(rig.In<Grid>("TranscribingRow").IsVisible);
        Assert.False(rig.In<Grid>("RecordingRow").IsVisible);
        var cancel = rig.In<Button>("CancelTranscribingButton");
        Assert.Equal(Strings.Voice_Cancel, AutomationProperties.GetName(cancel));
        Assert.Equal(Strings.Voice_CancelTranscribeTip, ToolTip.GetTip(cancel));
        Assert.True(rig.InsideBar(cancel));
        Assert.Equal("正在辨識語音…", rig.In<Grid>("TranscribingRow").GetVisualDescendants().OfType<TextBlock>().First().Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Failed_ShowsOneSentence_WithRetryAndDiscard_InsideTheBar()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.LastFailureMessage = "服務暫時無法使用";
        rig.Voice.State = VoiceInputState.Failed;
        rig.Refresh();

        Assert.True(rig.In<Grid>("FailedRow").IsVisible);
        Assert.Equal(SpriteVoiceBarViewModel.FailureSentence("服務暫時無法使用"), rig.In<TextBlock>("FailureLine").Text);

        var retry = rig.In<Button>("RetryVoiceButton");
        var discard = rig.In<Button>("DiscardVoiceButton");
        Assert.Equal(Strings.Voice_Retry, AutomationProperties.GetName(retry));
        Assert.Equal(Strings.Voice_RetryTip, ToolTip.GetTip(retry));
        Assert.Equal(Strings.Voice_DiscardTip, AutomationProperties.GetName(discard));
        Assert.Equal(Strings.Voice_DiscardTip, ToolTip.GetTip(discard));
        Assert.Contains("primary", retry.Classes);
        Assert.Contains("tertiary", discard.Classes);
        Assert.True(rig.InsideBar(retry) && rig.InsideBar(discard) && rig.InsideBar(rig.In<TextBlock>("FailureLine")));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Idle_ShowsTheInputRow_AndNoVoiceRow()
    {
        var rig = new Rig();
        rig.Refresh();

        Assert.True(rig.Named<Grid>("InputRow").IsVisible);
        Assert.False(rig.Named<Control>("VoiceBar").IsVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Escape_WhileRecording_CancelsTheRecording()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();
        rig.In<Button>("StopEditButton").Focus();

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.Contains("cancel", rig.Voice.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Escape_WhileTranscribing_DoesNotCancel()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Transcribing;
        rig.Refresh();

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

        Assert.DoesNotContain("cancel", rig.Voice.Calls);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void Recording_MovesFocusToStopAndEdit()
    {
        var rig = new Rig();
        rig.Voice.Origin = VoiceOrigin.Sprite;
        rig.Voice.State = VoiceInputState.Recording;
        rig.Refresh();

        Assert.Same(rig.In<Button>("StopEditButton"), rig.Window.FocusManager?.GetFocusedElement());
        rig.Window.Close();
    }

    // ---- 語音標籤與草稿 ----

    [AvaloniaFact]
    public void AVoiceDraft_ShowsTheLabel_InsideTheBar_AndMovesTheTextRight()
    {
        var rig = new Rig();
        var input = rig.Named<TextBox>("Input");
        var before = input.TranslatePoint(default, rig.Bar);

        rig.Window.InsertVoiceDraft("明天會下雨嗎");
        rig.Refresh();

        var chip = rig.Named<Border>("VoiceChip");
        Assert.True(chip.IsVisible);
        Assert.Equal(Strings.Sprite_VoiceLabel, AutomationProperties.GetName(chip));
        Assert.True(rig.InsideBar(chip));
        Assert.True(rig.InsideBar(input));
        Assert.True(input.TranslatePoint(default, rig.Bar)!.Value.X > before!.Value.X);
        Assert.True(chip.TranslatePoint(default, rig.Bar)!.Value.X + chip.Bounds.Width <= input.TranslatePoint(default, rig.Bar)!.Value.X);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void WithoutADraft_TheLabelIsHidden_AndTheInputKeepsItsPlace()
    {
        var rig = new Rig();
        rig.Refresh();

        Assert.False(rig.Named<Border>("VoiceChip").IsVisible);
        Assert.Equal(new Point(51, 18), rig.Named<TextBox>("Input").TranslatePoint(default, rig.Bar));
        rig.Window.Close();
    }
}
