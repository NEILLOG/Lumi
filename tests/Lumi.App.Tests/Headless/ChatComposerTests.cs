using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>UB-4：ChatComposer 兩層版面——外框、輸入框、下層工具列、麥克風佔位、送出／停止、回應中的提示、焦點與拖放外框。</summary>
public class ChatComposerTests
{
    private sealed class Rig
    {
        public FakeContextInspector Inspector { get; } = new();
        public ContextBarViewModel Bar { get; }
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public ChatComposer Composer { get; }

        public Rig(SequenceChatProvider? provider = null)
        {
            provider ??= new SequenceChatProvider(SequenceChatProvider.Answer("好"));
            var store = new InMemoryConversationStore();
            var contextStore = new InMemoryConversationContextStore();
            var time = new FakeTimeProvider();
            var builder = new FakeContextBuilder();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(provider, store, catalog, new ToolRegistry([]),
                new ToolApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance,
                contextStore, new EmptyFileScopeFactory(), builder);
            Bar = new ContextBarViewModel(contextStore, store, Inspector, builder, new HeuristicTokenEstimator(), new FakeFilePicker(),
                new FakeClipboardImage(), new FakeAttachmentStore(), time);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
                contextBar: Bar);
            Window = new WorkbenchWindow(Vm);
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
    }

    private static Geometry Icon(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value), $"找不到圖示資源 {key}");
        return Assert.IsAssignableFrom<Geometry>(value);
    }

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static Color Token(Control host, string key)
    {
        Assert.True(host.TryFindResource(key, host.ActualThemeVariant, out var value), $"找不到 token {key}");
        return Assert.IsType<Color>(value);
    }

    private static double TopOf(Control control, Visual root) => control.TranslatePoint(default, root)!.Value.Y;

    // ---- 外框與兩層結構 ----

    [AvaloniaFact]
    public async Task TheFrame_IsASurfaceBorderWithRadius16_AndMarginsAlignedWithTheConversation()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var area = rig.Named<Border>("InputArea");
        rig.Named<Button>("AddFileButton").Focus();   // 平時（輸入框沒有焦點）的外框
        rig.Window.UpdateLayout();

        Assert.Contains("composer-area", area.Classes);
        Assert.Equal(new CornerRadius(16), area.CornerRadius);
        Assert.Equal(new Thickness(1), area.BorderThickness);
        Assert.Equal(64, area.Margin.Left);
        Assert.Equal(64, area.Margin.Right);
        Assert.Equal(Token(area, "LumiSurfaceColor"), ColorOf(area.Background));
        Assert.Equal(Token(area, "LumiBorderColor"), ColorOf(area.BorderBrush));
        // 和對話區的左右 Padding 對齊
        var page = rig.Window.FindByName<ConversationView>("Conversation").FindByName<Border>("ConversationPage");
        Assert.Equal(page.Padding.Left, area.Margin.Left);
        Assert.Equal(page.Padding.Right, area.Margin.Right);
        Assert.True(area.GetValue(DragDrop.AllowDropProperty));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheInputBox_HasTheComposerSizes_NoBorderOfItsOwn_AndKeepsItsWatermark()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var input = rig.Named<TextBox>("InputBox");

        Assert.Equal(64, input.MinHeight);
        Assert.Equal(160, input.MaxHeight);
        Assert.True(input.AcceptsReturn);
        Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
        Assert.Equal(Strings.Workbench_InputPlaceholder, input.Watermark);
        Assert.Contains("composer", input.Classes);
        Assert.True(input.Bounds.Height >= 64);

        // 沒有自己的外框：平時、焦點時，內部邊框都是透明、粗細 0，也沒有光暈
        foreach (var focused in new[] { false, true })
        {
            if (focused) input.Focus();
            rig.Window.UpdateLayout();
            var border = input.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
            Assert.Equal(Colors.Transparent, ColorOf(border.Background));
            Assert.Equal(Colors.Transparent, ColorOf(border.BorderBrush));
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.False(input.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FocusGlow").IsVisible);
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheLayers_AreChipsThenInputThenTheToolbar()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Window.UpdateLayout();

        var area = rig.Named<Border>("InputArea");
        var bar = rig.Named<StackPanel>("ContextBar");
        var input = rig.Named<TextBox>("InputBox");
        var toolbar = rig.Named<Grid>("ComposerToolbar");

        Assert.True(bar.IsEffectivelyVisible);
        Assert.True(TopOf(bar, area) < TopOf(input, area));
        Assert.True(TopOf(input, area) + input.Bounds.Height <= TopOf(toolbar, area) + 0.5);
        // 三個加入按鈕與送出鈕都在同一個下層工具列裡
        foreach (var name in new[] { "AddFileButton", "AddFolderButton", "MicButton", "SendButton", "StopButton", "SendHint" })
            Assert.Contains(toolbar.GetVisualDescendants(), v => v is Control c && c.Name == name);
        rig.Window.Close();
    }

    // ---- 下層工具列 ----

    [AvaloniaFact]
    public async Task TheToolButtons_AreTertiary_WithIconTextTooltipAndAccessibleName()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var expected = new[]
        {
            ("AddFileButton", Strings.Context_AddFile, "LumiIconPaperclip"),
            ("AddFolderButton", Strings.Context_AddFolder, "LumiIconFolder"),
            ("MicButton", Strings.Composer_Mic, "LumiIconMic"),
        };

        foreach (var (name, label, iconKey) in expected)
        {
            var button = rig.Named<Button>(name);
            Assert.Contains("tertiary", button.Classes);
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(label, AutomationProperties.GetName(button));
            Assert.NotNull(ToolTip.GetTip(button));
            Assert.Same(Icon(iconKey), button.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
        }
        Assert.Equal(Strings.Context_AddFile, ToolTip.GetTip(rig.Named<Button>("AddFileButton")));
        Assert.Equal(Strings.Context_AddFolder, ToolTip.GetTip(rig.Named<Button>("AddFolderButton")));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheMicrophone_IsADisabledPlaceholder_WithATooltipAndAVisibleCaption()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var mic = rig.Named<Button>("MicButton");
        var caption = rig.Named<TextBlock>("MicCaption");

        Assert.False(mic.IsEffectivelyEnabled);
        Assert.Null(mic.Command);
        Assert.Equal(Strings.Composer_MicSoon, ToolTip.GetTip(mic));
        Assert.True(ToolTip.GetShowOnDisabled(mic));
        Assert.True(caption.IsEffectivelyVisible);
        Assert.Equal(Strings.Composer_MicSoon, caption.Text);
        Assert.Contains("muted", caption.Classes);
        // 在麥克風按鈕旁邊（右側），同一水平帶
        Assert.True(caption.TranslatePoint(default, rig.Composer)!.Value.X >= mic.TranslatePoint(new Point(mic.Bounds.Width, 0), rig.Composer)!.Value.X - 0.5);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheSendHint_IsAMutedCaption_OnTheRightOfTheToolbar()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var hint = rig.Named<TextBlock>("SendHint");
        var toolbar = rig.Named<Grid>("ComposerToolbar");

        Assert.True(hint.IsEffectivelyVisible);
        Assert.Equal("Enter 傳送 · Shift + Enter 換行", hint.Text);
        Assert.Contains("muted", hint.Classes);
        Assert.Equal(11, hint.FontSize);   // Caption
        Assert.True(hint.TranslatePoint(default, toolbar)!.Value.X > toolbar.Bounds.Width / 2);
        Assert.True(hint.TranslatePoint(default, toolbar)!.Value.X + hint.Bounds.Width <= rig.Named<Button>("SendButton").TranslatePoint(default, toolbar)!.Value.X + 0.5);
        rig.Window.Close();
    }

    // ---- 送出／停止 ----

    [AvaloniaFact]
    public async Task SendAndStop_AreRoundIconButtons_AndSwitchWhileResponding()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.InitializeAsync();
        var send = rig.Named<Button>("SendButton");
        var stop = rig.Named<Button>("StopButton");

        Assert.True(send.IsEffectivelyVisible);
        Assert.False(stop.IsEffectivelyVisible);
        Assert.Contains("round", send.Classes);
        Assert.Equal(send.Width, send.Height);
        Assert.Equal(new CornerRadius(send.Width / 2), send.CornerRadius);
        Assert.Same(Icon("LumiIconArrowUp"), send.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        Assert.DoesNotContain(send.GetVisualDescendants().OfType<TextBlock>(), t => !string.IsNullOrEmpty(t.Text));
        Assert.Equal(Token(send, "LumiAccentColor"), ColorOf(send.Background));
        Assert.Equal(Token(send, "LumiOnAccentColor"), ColorOf(send.Foreground));
        Assert.Equal(Strings.Workbench_Send, ToolTip.GetTip(send));
        Assert.Equal(Strings.Workbench_Send, AutomationProperties.GetName(send));
        Assert.Same(rig.Vm.SendCommand, send.Command);

        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);
        rig.Window.UpdateLayout();

        Assert.False(send.IsEffectivelyVisible);
        Assert.True(stop.IsEffectivelyVisible);
        Assert.Same(Icon("LumiIconSquare"), stop.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        Assert.Equal(Token(stop, "LumiPrimaryButtonColor"), ColorOf(stop.Background));
        Assert.Equal(Strings.Workbench_Stop, ToolTip.GetTip(stop));
        Assert.Equal(Strings.Workbench_Stop, AutomationProperties.GetName(stop));
        Assert.Same(rig.Vm.StopCommand, stop.Command);

        await rig.Vm.StopAndWaitAsync();
        await sending;
        rig.Window.UpdateLayout();
        Assert.True(send.IsEffectivelyVisible);
        Assert.False(stop.IsEffectivelyVisible);
        rig.Window.Close();
    }

    // ---- 回應中 ----

    [AvaloniaFact]
    public async Task WhileResponding_TheHintAreaExplains_AndTheAddButtonsAreDisabledWithATooltip()
    {
        var rig = new Rig(new SequenceChatProvider(SequenceChatProvider.Answer("好")) { HangOnCall = 1 });
        await rig.InitializeAsync();
        var hint = rig.Named<TextBlock>("SendHint");
        var responding = rig.Named<TextBlock>("RespondingHint");
        var file = rig.Named<Button>("AddFileButton");
        var folder = rig.Named<Button>("AddFolderButton");
        var toolbar = rig.Named<Grid>("ComposerToolbar");
        var heightBefore = rig.Named<Border>("InputArea").Bounds.Height;
        Assert.False(responding.IsEffectivelyVisible);
        Assert.True(file.IsEffectivelyEnabled);

        rig.Vm.InputText = "x";
        var sending = rig.Vm.SendCommand.ExecuteAsync(null);
        await SettingsHost.WaitUntil(() => rig.Vm.IsResponding);
        rig.Window.UpdateLayout();

        Assert.False(hint.IsEffectivelyVisible);
        Assert.True(responding.IsEffectivelyVisible);
        Assert.Equal(Strings.Composer_RespondingHint, responding.Text);
        Assert.Contains("muted", responding.Classes);
        // 提示在同一個提示文字區：不另外長出一行
        Assert.Equal(heightBefore, rig.Named<Border>("InputArea").Bounds.Height, 0.5);
        Assert.True(responding.TranslatePoint(default, toolbar)!.Value.X + responding.Bounds.Width <= rig.Named<Button>("StopButton").TranslatePoint(default, toolbar)!.Value.X + 0.5);
        Assert.DoesNotContain(rig.Composer.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == "回應結束後才能送出");
        foreach (var button in new[] { file, folder })
        {
            Assert.False(button.IsEffectivelyEnabled);
            Assert.Equal(Strings.Composer_AddBlocked, ToolTip.GetTip(button));
            Assert.True(ToolTip.GetShowOnDisabled(button));
        }

        await rig.Vm.StopAndWaitAsync();
        await sending;
        rig.Window.UpdateLayout();

        Assert.True(hint.IsEffectivelyVisible);
        Assert.False(responding.IsEffectivelyVisible);
        Assert.True(file.IsEffectivelyEnabled);
        Assert.Equal(Strings.Context_AddFile, ToolTip.GetTip(file));
        Assert.Equal(Strings.Context_AddFolder, ToolTip.GetTip(folder));
        rig.Window.Close();
    }

    // ---- 上下文 chip ----

    [AvaloniaFact]
    public async Task TheChipRemoveButton_IsATertiaryIconButton_WithTooltipAndAccessibleName()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Window.UpdateLayout();

        var remove = rig.Named<ItemsControl>("ContextChips").GetVisualDescendants().OfType<Button>().Single();
        Assert.Contains("tertiary", remove.Classes);
        Assert.Equal(Strings.Context_Remove, ToolTip.GetTip(remove));
        Assert.Equal(Strings.Context_Remove, AutomationProperties.GetName(remove));
        Assert.Same(Icon("LumiIconX"), remove.GetVisualDescendants().OfType<LumiIcon>().Single().Data);
        Assert.Null(remove.Content as string);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheOverLimitWarning_IsAnAlertIcon_NotAUnicodeSymbol()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        await rig.InitializeAsync();
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        var icon = rig.Named<LumiIcon>("OverLimitIcon");
        Assert.False(icon.IsEffectivelyVisible);

        rig.Bar.ContextWindow = 10;
        rig.Window.UpdateLayout();

        Assert.True(icon.IsEffectivelyVisible);
        Assert.Same(Icon("LumiIconAlertTriangle"), icon.Data);
        Assert.Equal(rig.Bar.OverLimitText, ToolTip.GetTip(icon));
        Assert.DoesNotContain(rig.Named<StackPanel>("ContextBar").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "⚠");
        rig.Window.Close();
    }

    // ---- 焦點與拖放外框 ----

    [AvaloniaFact]
    public async Task WhenTheInputBoxHasFocus_TheOuterFrameGetsTheAccentBorder()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var area = rig.Named<Border>("InputArea");
        var input = rig.Named<TextBox>("InputBox");
        rig.Named<Button>("AddFileButton").Focus();   // 工作台開啟時輸入框可能已經有焦點，先移開
        rig.Window.UpdateLayout();
        Assert.DoesNotContain("focused", area.Classes);
        Assert.Equal(Token(area, "LumiBorderColor"), ColorOf(area.BorderBrush));

        input.Focus();
        rig.Window.UpdateLayout();
        Assert.Contains("focused", area.Classes);
        Assert.Equal(Token(area, "LumiAccentColor"), ColorOf(area.BorderBrush));
        Assert.Equal(new Thickness(1), area.BorderThickness);

        rig.Named<Button>("AddFileButton").Focus();
        rig.Window.UpdateLayout();
        Assert.DoesNotContain("focused", area.Classes);
        Assert.Equal(Token(area, "LumiBorderColor"), ColorOf(area.BorderBrush));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheDroppingClass_ShowsATwoPixelAccentBorder_EvenWhileFocused()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var area = rig.Named<Border>("InputArea");
        rig.Named<TextBox>("InputBox").Focus();

        area.Classes.Set("dropping", true);
        rig.Window.UpdateLayout();
        Assert.Equal(new Thickness(2), area.BorderThickness);
        Assert.Equal(Token(area, "LumiAccentColor"), ColorOf(area.BorderBrush));

        area.Classes.Set("dropping", false);
        rig.Window.UpdateLayout();
        Assert.Equal(new Thickness(1), area.BorderThickness);
        rig.Window.Close();
    }

    // ---- 既有行為 ----

    [AvaloniaFact]
    public async Task Enter_Sends_AndShiftEnter_DoesNot()
    {
        var rig = new Rig();
        await rig.InitializeAsync();
        var input = rig.Named<TextBox>("InputBox");
        input.Focus();
        rig.Vm.InputText = "你好";
        rig.Window.UpdateLayout();

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.Shift, PhysicalKey.Enter, null);
        Assert.Empty(rig.Vm.Messages);

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await SettingsHost.WaitUntil(() => rig.Vm.Messages.Count > 0);
        Assert.Contains(rig.Vm.Messages, m => m.IsUser);
        rig.Window.Close();
    }
}
