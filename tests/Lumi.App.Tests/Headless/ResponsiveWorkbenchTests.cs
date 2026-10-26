using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UC-1：工作台在 Wide／Compact／Narrow 三種寬度的版面（RWD原則 第 2、7、8 節）。
/// 每個尺寸都用新視窗：視窗尺寸是 Show 之前設定的；跨越斷點的 hysteresis 另外用同一個視窗縮放來測。
/// </summary>
public class ResponsiveWorkbenchTests
{
    private const double LongTextWidthTolerance = 1.0;

    private sealed class Rig
    {
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }
        public FakeModelCatalog Catalog { get; } = new();

        public Rig(double width, double height, string? modelId = null)
        {
            if (modelId is not null)
                Catalog.Current = Catalog.Current with { Models = [FakeModelCatalog.Model with { ModelId = modelId }] };
            var store = new InMemoryConversationStore();
            var longText = string.Concat(Enumerable.Repeat("這是一段很長的回答，用來把訊息撐到寬度上限。This sentence is long enough to wrap. ", 12));
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer(longText)), store, Catalog);
            Vm = new WorkbenchViewModel(service, store, Catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm) { Width = width, Height = height };
            Window.Show();
            Window.UpdateLayout();
        }

        public async Task SendAsync()
        {
            await Vm.InitializeAsync();
            Vm.InputText = string.Concat(Enumerable.Repeat("使用者也送出一段很長的訊息，確認寬度比例。The user message is long as well. ", 12));
            await Vm.SendCommand.ExecuteAsync(null);
            Window.UpdateLayout();
        }

        public void Resize(double width, double height)
        {
            // Headless 的 Window.Width／Height 在 Show 之後不會觸發 Bounds 變更，要直接要求平台視窗改尺寸（等同使用者拖曳邊框）
            Window.Width = width;
            Window.Height = height;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Window.UpdateLayout();
        }

        public ChatToolbar Toolbar => Window.FindByName<ChatToolbar>("Toolbar");
        public ConversationView Conversation => Window.FindByName<ConversationView>("Conversation");
        public ChatComposer Composer => Window.FindByName<ChatComposer>("Composer");
        public CustomTitleBar TitleBar => Window.FindByName<CustomTitleBar>("TitleBar");
    }

    private static string WidthClassOf(Window window) =>
        Assert.Single(window.Classes, c => c is "wide" or "compact" or "narrow");

    /// <summary>按鈕裡看得到的文字（不含被隱藏的）。</summary>
    private static List<string> VisibleTexts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).Select(t => t.Text!).ToList();

    private static Rect BoundsIn(Visual visual, Visual ancestor)
    {
        var origin = visual.TranslatePoint(default, ancestor)!.Value;
        return new Rect(origin, visual.Bounds.Size);
    }

    public static TheoryData<double, double, string> Sizes => new()
    {
        { 1200, 800, "wide" },
        { 900, 700, "compact" },
        { 700, 650, "compact" },
        { 620, 650, "compact" },
        { 619, 650, "narrow" },
        { 520, 650, "narrow" },
        { 500, 650, "narrow" },
    };

    [AvaloniaFact]
    public void TheWorkbench_MinWidthIs500()
    {
        var rig = new Rig(900, 700);
        Assert.Equal(500, rig.Window.MinWidth);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task EverySize_HasTheRightClass_NoHorizontalOverflow_AndTheComposerIsFullyVisible(double width, double height, string expected)
    {
        var rig = new Rig(width, height);
        await rig.SendAsync();
        var window = rig.Window;

        Assert.Equal(expected, WidthClassOf(window));
        Assert.Equal(width, window.Bounds.Width, 0.5);

        // 對話區沒有水平捲動，也沒有超出視窗
        var scroller = rig.Conversation.FindByName<ScrollViewer>("MessageScroller");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, $"Extent {scroller.Extent.Width} > Viewport {scroller.Viewport.Width}");
        var page = rig.Conversation.FindByName<Border>("ConversationPage");
        Assert.True(page.Bounds.Width <= width + 0.5);
        Assert.True(BoundsIn(page, window).Right <= width + 0.5);

        // 輸入框、送出鈕完整在視窗內
        var area = rig.Composer.FindByName<Border>("InputArea");
        var input = rig.Composer.FindByName<TextBox>("InputBox");
        var send = rig.Composer.FindByName<Button>("SendButton");
        foreach (var control in new Control[] { area, input, send })
        {
            var bounds = BoundsIn(control, window);
            Assert.True(bounds.Left >= -0.5, $"{control.Name} left {bounds.Left}");
            Assert.True(bounds.Right <= width + 0.5, $"{control.Name} right {bounds.Right} > {width}");
            Assert.True(bounds.Bottom <= window.Bounds.Height + 0.5, $"{control.Name} bottom {bounds.Bottom}");
        }
        Assert.True(send.IsEffectivelyVisible);
        Assert.True(BoundsIn(send, area).Right <= area.Bounds.Width + 0.5);

        // 工具列上的按鈕都在視窗內，彼此不重疊
        var toolbar = rig.Toolbar;
        var new_ = BoundsIn(toolbar.NewConversationButton, window);
        var history = BoundsIn(toolbar.HistoryButton, window);
        var capsule = BoundsIn(toolbar.CapsuleButton, window);
        var more = BoundsIn(toolbar.MoreButton, window);
        Assert.True(new_.Right <= history.Left + 0.5);
        Assert.True(history.Right <= capsule.Left + 0.5);
        Assert.True(capsule.Right <= more.Left + 0.5);
        Assert.True(more.Right <= width + 0.5);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, "wide")]
    [InlineData(900, "compact")]
    [InlineData(700, "compact")]
    [InlineData(620, "compact")]
    [InlineData(619, "narrow")]
    [InlineData(520, "narrow")]
    public void TitleBar_SpriteModeButton_OnlyChangesItsLabel_AndKeepsItsTooltipAndName(double width, string cls)
    {
        var rig = new Rig(width, 650);
        var button = rig.TitleBar.FindByName<Button>("SpriteModeButton");
        var texts = VisibleTexts(button);

        switch (cls)
        {
            case "wide": Assert.Equal([Strings.Workbench_SpriteMode], texts); break;
            case "compact": Assert.Equal([Strings.Workbench_SpriteModeShort], texts); break;
            default: Assert.Empty(texts); break;
        }
        Assert.Contains(button.GetVisualDescendants().OfType<LumiIcon>(), i => i.IsEffectivelyVisible);
        Assert.Equal(Strings.Workbench_SpriteMode, ToolTip.GetTip(button));
        Assert.Equal(Strings.Workbench_SpriteMode, AutomationProperties.GetName(button));
        Assert.True(button.Bounds.Width >= 36 && button.Bounds.Height >= 32);
        // 位置固定在系統按鈕左側：右邊緣不變（Windows 的右側保留區寬度固定）
        Assert.True(BoundsIn(button, rig.Window).Right <= width + 0.5);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, true)]
    [InlineData(1000, true)]
    [InlineData(900, false)]
    [InlineData(520, false)]
    public void Toolbar_NewAndHistory_AreIconOnly_FromCompact(double width, bool showsText)
    {
        var rig = new Rig(width, 650);
        foreach (var (button, label) in new[] { (rig.Toolbar.NewConversationButton, Strings.Workbench_NewConversation), (rig.Toolbar.HistoryButton, Strings.Workbench_History) })
        {
            Assert.Equal(showsText, VisibleTexts(button).Contains(label));
            Assert.Contains(button.GetVisualDescendants().OfType<LumiIcon>(), i => i.IsEffectivelyVisible);
            Assert.Equal(label, ToolTip.GetTip(button));
            Assert.Equal(label, AutomationProperties.GetName(button));
            if (!showsText)
                Assert.True(button.Bounds.Width <= 32.5);
        }
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public void TheCapsule_AlwaysShowsTheExactModelId_AndTheProvider(double width, double height, string cls)
    {
        var rig = new Rig(width, height);
        var capsule = rig.Toolbar.CapsuleButton;
        var switcher = rig.Vm.ModelSwitcher;
        var texts = VisibleTexts(capsule);

        Assert.Contains(texts, t => t.Contains(switcher.ModelId));
        if (cls == "narrow")
        {
            // 供應商字首方塊 + model ID，完整「供應商 · model ID」在 Tooltip
            Assert.Equal("測", switcher.ProviderInitial);
            Assert.Contains(switcher.ProviderInitial, texts);
            Assert.DoesNotContain(texts, t => t == switcher.CapsuleText);
            var badge = rig.Toolbar.FindByName<Border>("ProviderInitialBadge");
            Assert.True(badge.IsEffectivelyVisible);
            Assert.Equal(20, badge.Bounds.Width);
            Assert.Equal(20, badge.Bounds.Height);
            Assert.Contains(switcher.ProviderName, (string)ToolTip.GetTip(capsule)!);
            Assert.Contains(switcher.ModelId, (string)ToolTip.GetTip(capsule)!);
        }
        else
        {
            Assert.Contains(texts, t => t == switcher.CapsuleText);
            Assert.False(rig.Toolbar.FindByName<Border>("ProviderInitialBadge").IsEffectivelyVisible);
        }
        Assert.Equal(switcher.CapsuleText, AutomationProperties.GetName(capsule));
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200)]
    [InlineData(700)]
    [InlineData(520)]
    public void ALongModelId_IsEllipsized_AtEveryWidth_AndKeepsTheOtherButtonsInside(double width)
    {
        var rig = new Rig(width, 650, "provider/" + new string('x', 200));
        var capsule = BoundsIn(rig.Toolbar.CapsuleButton, rig.Window);
        var more = BoundsIn(rig.Toolbar.MoreButton, rig.Window);
        var history = BoundsIn(rig.Toolbar.HistoryButton, rig.Window);

        Assert.True(capsule.Left >= history.Right - 0.5);
        Assert.True(capsule.Right <= more.Left + 0.5);
        Assert.True(more.Right <= width + 0.5);
        var text = rig.Toolbar.CapsuleButton.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.Text!.Contains("xxx"));
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, text.TextTrimming);
        Assert.StartsWith(width < 620 ? "provider/" : "測試", text.Text!.TrimStart());
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, 64, 64)]
    [InlineData(900, 24, 24)]
    [InlineData(620, 24, 24)]
    [InlineData(619, 16, 16)]
    [InlineData(520, 16, 16)]
    public void ConversationPaddingAndComposerMargin_FollowTheBreakpoint(double width, double pagePadding, double composerMargin)
    {
        var rig = new Rig(width, 650);
        Assert.Equal(new Thickness(pagePadding, 24), rig.Conversation.FindByName<Border>("ConversationPage").Padding);
        Assert.Equal(new Thickness(composerMargin, 8, composerMargin, 16), rig.Composer.FindByName<Border>("InputArea").Margin);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, true)]
    [InlineData(900, false)]
    [InlineData(520, false)]
    public async Task Composer_Buttons_AreIconOnly_AndTheHintsAreHidden_FromCompact(double width, bool wide)
    {
        var rig = new Rig(width, 650);
        await rig.Vm.InitializeAsync();
        var composer = rig.Composer;

        foreach (var (name, label) in new[] { ("AddFileButton", Strings.Context_AddFile), ("AddFolderButton", Strings.Context_AddFolder), ("MicButton", Strings.Composer_Mic) })
        {
            var button = composer.FindByName<Button>(name);
            Assert.Equal(wide, VisibleTexts(button).Contains(label));
            Assert.Contains(button.GetVisualDescendants().OfType<LumiIcon>(), i => i.IsEffectivelyVisible);
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(button) as string));
            Assert.Equal(label, AutomationProperties.GetName(button));
            if (!wide)
                Assert.True(button.Bounds.Width <= 32.5);
        }
        Assert.Equal(wide, composer.FindByName<TextBlock>("SendHint").IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(900)]
    [InlineData(520)]
    public async Task WhileResponding_TheRespondingHintStaysVisible_AndIsTrimmed(double width)
    {
        var rig = new Rig(width, 650);
        await rig.Vm.InitializeAsync();
        rig.Vm.IsResponding = true;
        rig.Window.UpdateLayout();

        var hint = rig.Composer.FindByName<TextBlock>("RespondingHint");
        var area = rig.Composer.FindByName<Border>("InputArea");
        Assert.True(hint.IsEffectivelyVisible);
        Assert.False(rig.Composer.FindByName<TextBlock>("SendHint").IsEffectivelyVisible);
        Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, hint.TextTrimming);
        Assert.True(BoundsIn(hint, area).Right <= area.Bounds.Width + 0.5);
        Assert.True(BoundsIn(rig.Composer.FindByName<Button>("StopButton"), rig.Window).Right <= width + 0.5);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, 0.72, 820.0)]
    [InlineData(900, 0.80, double.PositiveInfinity)]
    [InlineData(700, 0.80, double.PositiveInfinity)]
    [InlineData(520, 0.80, double.PositiveInfinity)]
    public async Task MessageWidths_FollowTheContentArea(double width, double userFraction, double assistantCap)
    {
        var rig = new Rig(width, 800);
        await rig.SendAsync();
        var view = rig.Conversation;

        var user = view.GetVisualDescendants().OfType<FractionWidthLimiter>().Single(l => l.Classes.Contains("user") && l.IsEffectivelyVisible);
        var assistant = view.GetVisualDescendants().OfType<FractionWidthLimiter>().Single(l => l.Classes.Contains("assistant") && l.IsEffectivelyVisible);
        var assistantFraction = width >= 1000 ? 1.0 : 0.92;

        Assert.Equal(userFraction, user.Fraction);
        Assert.Equal(assistantFraction, assistant.Fraction);
        Assert.Equal(assistantCap, assistant.MaxPixels);

        // 內容區寬度：限制器本身就是撐滿內容區的那一層
        var content = user.Bounds.Width;
        Assert.Equal(content, assistant.Bounds.Width, 0.5);
        var pagePadding = width >= 1000 ? 64 : width >= 620 ? 24 : 16;
        Assert.InRange(content, width - 2 * pagePadding - 20, width - 2 * pagePadding + 0.5);   // 扣掉捲軸可能占用的寬度

        var userBox = user.Child!;
        var assistantBox = assistant.Child!;
        var userLimit = content * userFraction;
        var assistantLimit = Math.Min(assistantCap, content * assistantFraction);
        Assert.True(userBox.Bounds.Width <= userLimit + 1, $"user {userBox.Bounds.Width} > {userLimit}");
        Assert.True(assistantBox.Bounds.Width <= assistantLimit + 1, $"assistant {assistantBox.Bounds.Width} > {assistantLimit}");
        // 訊息夠長時會用滿上限（沒有被提前縮窄）
        Assert.True(userBox.Bounds.Width >= userLimit - LongTextWidthTolerance * 40, $"user {userBox.Bounds.Width} << {userLimit}");
        Assert.True(assistantBox.Bounds.Width >= assistantLimit - LongTextWidthTolerance * 40, $"assistant {assistantBox.Bounds.Width} << {assistantLimit}");
        // 使用者靠右、助理靠左，不論多窄都不會變成滿版
        Assert.True(BoundsIn(userBox, view).Right >= BoundsIn(user, view).Right - 0.5);
        Assert.True(BoundsIn(assistantBox, view).Left <= BoundsIn(assistant, view).Left + 0.5);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(900, false)]
    [InlineData(520, true)]
    public async Task MessageActions_AreIconOnly_FromCompact_AndNarrowHidesTimesAndYouAndShrinksTheAvatar(double width, bool narrow)
    {
        var rig = new Rig(width, 800);
        await rig.SendAsync();
        var view = rig.Conversation;

        rig.Vm.ShowTokenUsage = true;   // token 文字預設不顯示（進階開關）
        view.UpdateLayout();
        var actions = view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "MessageActions" && p.IsEffectivelyVisible);
        var texts = VisibleTexts(actions);
        Assert.DoesNotContain(Strings.Workbench_Regenerate, texts);
        Assert.DoesNotContain(Strings.Workbench_Copy, texts);
        Assert.Contains(actions.Children.OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text == rig.Vm.Messages.Last().UsageText);   // token 文字保留
        foreach (var button in actions.Children.OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(button) as string));
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)));
        }
        Assert.True(BoundsIn(actions, rig.Window).Right <= width + 0.5);

        var allVisible = VisibleTexts(view);
        var assistantTime = rig.Vm.Messages.Single(m => m.IsAssistant).TimeText;
        Assert.Equal(!narrow, allVisible.Contains(assistantTime));
        Assert.Equal(!narrow, allVisible.Contains(Strings.Workbench_You));
        Assert.Contains(Strings.Workbench_AssistantName, allVisible);   // 助理名稱保留
        var avatar = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("avatar") && b.IsEffectivelyVisible);
        Assert.Equal(narrow ? 24 : 32, avatar.Bounds.Width);
        Assert.Equal(narrow ? 24 : 32, avatar.Bounds.Height);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, true)]
    [InlineData(700, true)]
    [InlineData(619, false)]
    public async Task WelcomeSubtitle_IsHiddenInNarrow(double width, bool visible)
    {
        var rig = new Rig(width, 800);
        await rig.Vm.InitializeAsync();
        rig.Window.UpdateLayout();

        Assert.True(rig.Conversation.FindByName<StackPanel>("WelcomePanel").IsEffectivelyVisible);
        Assert.Equal(visible, rig.Conversation.FindByName<TextBlock>("WelcomeSubtitle").IsEffectivelyVisible);
        Assert.True(rig.Conversation.FindByName<TextBlock>("WelcomeTitle").IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, SplitViewDisplayMode.Inline)]
    [InlineData(700, SplitViewDisplayMode.Inline)]
    [InlineData(620, SplitViewDisplayMode.Inline)]
    [InlineData(619, SplitViewDisplayMode.Overlay)]
    [InlineData(520, SplitViewDisplayMode.Overlay)]
    public void HistoryDrawer_IsInlineInWideAndCompact_AndOverlayInNarrow(double width, SplitViewDisplayMode expected)
    {
        var rig = new Rig(width, 650);
        var split = rig.Window.GetVisualDescendants().OfType<SplitView>().Single();
        Assert.Equal(expected, split.DisplayMode);

        // 抽屜打開時，輸入框仍完整在視窗內（Overlay 不把訊息區往右推）
        rig.Vm.History.ToggleCommand.Execute(null);
        rig.Window.UpdateLayout();
        Assert.True(split.IsPaneOpen);
        var area = BoundsIn(rig.Composer.FindByName<Border>("InputArea"), rig.Window);
        Assert.True(area.Right <= width + 0.5);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ResizingAcrossTheBreakpoints_SwitchesTheLayoutOnce_AndHasHysteresis()
    {
        var rig = new Rig(1200, 800);
        var window = rig.Window;
        Assert.Equal("wide", WidthClassOf(window));

        rig.Resize(900, 700);
        Assert.Equal("compact", WidthClassOf(window));
        Assert.Equal(new Thickness(24, 24), rig.Conversation.FindByName<Border>("ConversationPage").Padding);
        Assert.Empty(VisibleTexts(rig.Toolbar.NewConversationButton));

        rig.Resize(620, 650);
        Assert.Equal("compact", WidthClassOf(window));
        rig.Resize(619, 650);   // 已經是 Compact：緩衝帶內不切換
        Assert.Equal("compact", WidthClassOf(window));
        rig.Resize(611, 650);
        Assert.Equal("narrow", WidthClassOf(window));
        Assert.Equal(SplitViewDisplayMode.Overlay, window.GetVisualDescendants().OfType<SplitView>().Single().DisplayMode);
        Assert.Contains(rig.Vm.ModelSwitcher.ModelId, string.Join("|", VisibleTexts(rig.Toolbar.CapsuleButton)));

        rig.Resize(627, 650);   // 從 Narrow 回來：要到 628 才算 Compact
        Assert.Equal("narrow", WidthClassOf(window));
        rig.Resize(628, 650);
        Assert.Equal("compact", WidthClassOf(window));
        Assert.Equal(SplitViewDisplayMode.Inline, window.GetVisualDescendants().OfType<SplitView>().Single().DisplayMode);

        rig.Resize(1000, 800);
        Assert.Equal("wide", WidthClassOf(window));
        Assert.Equal(new Thickness(64, 24), rig.Conversation.FindByName<Border>("ConversationPage").Padding);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ResizingAcrossBreakpoints_DoesNotRebuildTheConversationList()
    {
        var rig = new Rig(1200, 800);
        await rig.SendAsync();
        var before = rig.Conversation.GetVisualDescendants().OfType<FractionWidthLimiter>().ToList();
        Assert.NotEmpty(before);

        rig.Resize(700, 700);
        rig.Resize(520, 650);
        rig.Resize(1200, 800);

        var after = rig.Conversation.GetVisualDescendants().OfType<FractionWidthLimiter>().ToList();
        Assert.Equal(before.Count, after.Count);
        Assert.All(before.Zip(after), pair => Assert.Same(pair.First, pair.Second));
        rig.Window.Close();
    }
}
