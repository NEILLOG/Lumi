using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.Core.Chat;

namespace Lumi.App.Tests.Sprites;

/// <summary>小精靈的錯誤氣泡（headless）：關閉鈕、繼續／重試的文字。</summary>
public class SpriteErrorBubbleHeadlessTests
{
    private static (SpriteBubbleView View, SpriteBubbleViewModel Vm, Window Window, int[] Dismissals) Bubble()
    {
        var vm = new SpriteBubbleViewModel(new SpriteShellOptions(), new FakeTimeProvider(), new RecordingResponder());
        var dismissals = new int[1];
        vm.Dismissed += () => dismissals[0]++;
        var view = new SpriteBubbleView { DataContext = vm };
        var window = new Window { Width = 340, Height = 400, Content = view };
        window.Show();
        return (view, vm, window, dismissals);
    }

    [AvaloniaFact]
    public void ErrorBubble_HasACloseButton_NamedClose_ThatDismissesIt()
    {
        var (view, vm, window, dismissals) = Bubble();
        vm.OnRunStarted();
        vm.OnRunFinished(Ev.Finished(Ev.Failure()));
        window.UpdateLayout();

        var close = view.FindControl<Button>("ErrorDismissButton")!;
        Assert.True(close.IsEffectivelyVisible);
        Assert.Equal(Strings.Sprite_DismissError, AutomationProperties.GetName(close));
        Assert.Equal(Strings.Sprite_DismissError, ToolTip.GetTip(close));

        // 真的按一下（按下與放開），確認命令有接上
        var center = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);

        Assert.Equal(SpriteBubbleKind.Hidden, vm.Kind);
        Assert.Equal(1, dismissals[0]);
        window.Close();
    }

    [AvaloniaFact]
    public void VoiceFailure_HasNoCloseButton()
    {
        var (view, vm, window, _) = Bubble();
        vm.ShowVoiceFailure("轉寫失敗");
        window.UpdateLayout();

        Assert.False(view.FindControl<Button>("ErrorDismissButton")!.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ToolLoopLimit_ShowsContinueOnTheRetryButton()
    {
        var (view, vm, window, _) = Bubble();
        vm.OnRunStarted();
        vm.OnRunFinished(Ev.Finished(Ev.Failure(ChatFailureKind.ToolLoopLimit, "已經連續使用工具 10 輪", retryable: true)));
        window.UpdateLayout();

        var retry = view.FindControl<Button>("RetryButton")!;
        Assert.Equal(Strings.Sprite_Continue, retry.Content);
        Assert.Equal(Strings.Sprite_Continue, AutomationProperties.GetName(retry));
        window.Close();
    }
}
