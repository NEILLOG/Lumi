using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UC-2：高度規則（short／veryshort）、預設視窗尺寸、工作台的可存取名稱與 Tab 順序（RWD原則 第 5、7、8 節）。
/// </summary>
public class HeightAndAccessibilityTests
{
    private sealed class Rig
    {
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }

        public Rig(double? width, double? height)
        {
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("這是回答。")), store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm);
            if (width is { } w) Window.Width = w;
            if (height is { } h) Window.Height = h;
            Window.Show();
            Window.UpdateLayout();
        }

        public async Task InitializeAsync()
        {
            await Vm.InitializeAsync();
            Window.UpdateLayout();
        }

        public async Task SendAsync()
        {
            await Vm.InitializeAsync();
            Vm.InputText = "你好";
            await Vm.SendCommand.ExecuteAsync(null);
            Window.UpdateLayout();
        }

        public ConversationView Conversation => Window.FindByName<ConversationView>("Conversation");
        public ChatComposer Composer => Window.FindByName<ChatComposer>("Composer");
    }

    private static Rect BoundsIn(Visual visual, Visual ancestor) =>
        new(visual.TranslatePoint(default, ancestor)!.Value, visual.Bounds.Size);

    // ---- 預設視窗尺寸 ----

    [AvaloniaFact]
    public void TheDefaultWindow_Is1040x760_WhichIsWide()
    {
        var rig = new Rig(null, null);
        Assert.Equal(1040, rig.Window.Width);
        Assert.Equal(760, rig.Window.Height);
        Assert.Equal(420, rig.Window.MinHeight);
        Assert.Contains("wide", rig.Window.Classes);
        Assert.DoesNotContain("short", rig.Window.Classes);
        Assert.DoesNotContain("veryshort", rig.Window.Classes);
        rig.Window.Close();
    }

    // ---- 高度規則 ----

    public static TheoryData<double, double, string> HeightCases => new()
    {
        { 1200, 800, "normal" },
        { 800, 650, "short" },
        { 800, 560, "veryshort" },
        { 520, 420, "veryshort" },
    };

    private static void AssertHeightClass(Window window, string expected)
    {
        Assert.Equal(expected == "short", window.Classes.Contains("short"));
        Assert.Equal(expected == "veryshort", window.Classes.Contains("veryshort"));
    }

    [AvaloniaTheory]
    [MemberData(nameof(HeightCases))]
    public async Task TheComposer_IsFullyVisible_AndTheConversationKeepsUsableHeight(double width, double height, string cls)
    {
        var rig = new Rig(width, height);
        await rig.InitializeAsync();
        var window = rig.Window;
        AssertHeightClass(window, cls);

        var area = rig.Composer.FindByName<Border>("InputArea");
        var input = rig.Composer.FindByName<TextBox>("InputBox");
        var toolbar = rig.Composer.FindByName<Grid>("ComposerToolbar");
        var send = rig.Composer.FindByName<Button>("SendButton");

        // 輸入框、下層工具列、送出鈕都完整在視窗內
        foreach (var control in new Control[] { area, input, toolbar, send })
        {
            var bounds = BoundsIn(control, window);
            Assert.True(control.IsEffectivelyVisible, control.Name);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= window.Bounds.Height + 0.5, $"{control.Name} {bounds.Top}-{bounds.Bottom} / {window.Bounds.Height}");
            Assert.True(bounds.Left >= -0.5 && bounds.Right <= width + 0.5, $"{control.Name} {bounds.Left}-{bounds.Right}");
        }

        // 輸入框至少能輸入兩行文字；下層工具列一列按鈕（送出鈕 36 高）完整保留
        var lineHeight = input.FontSize * 1.2;
        Assert.True(input.Bounds.Height >= 2 * lineHeight, $"input {input.Bounds.Height}");
        Assert.True(toolbar.Bounds.Height >= 36, $"toolbar {toolbar.Bounds.Height}");
        Assert.True(send.Bounds.Height >= 36);
        Assert.True(input.Bounds.Height + toolbar.Bounds.Height <= area.Bounds.Height);

        // 對話區仍有可用高度
        var scroller = rig.Conversation.FindByName<ScrollViewer>("MessageScroller");
        Assert.True(scroller.Viewport.Height >= 150, $"viewport {scroller.Viewport.Height}");
        Assert.True(BoundsIn(scroller, window).Bottom <= BoundsIn(area, window).Top + 0.5);   // 對話區與輸入區不重疊
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, 64.0)]
    [InlineData(800, 650, 48.0)]
    [InlineData(800, 560, 48.0)]
    public void TheInputBox_DefaultHeight_IsTwoLinesFromShort(double width, double height, double minHeight)
    {
        var rig = new Rig(width, height);
        var input = rig.Composer.FindByName<TextBox>("InputBox");
        Assert.Equal(minHeight, input.MinHeight);
        Assert.Equal(160, input.MaxHeight);
        Assert.Equal(minHeight, input.Bounds.Height, 0.5);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, true, 32.0)]
    [InlineData(800, 650, true, 8.0)]
    [InlineData(800, 560, false, 8.0)]
    public async Task TheWelcomePanel_ShrinksInShort_AndIsHiddenInVeryShort(double width, double height, bool visible, double topMargin)
    {
        var rig = new Rig(width, height);
        await rig.InitializeAsync();

        var welcome = rig.Conversation.FindByName<StackPanel>("WelcomePanel");
        Assert.True(welcome.IsVisible);   // 資料上仍是「沒有訊息」
        Assert.Equal(visible, welcome.IsEffectivelyVisible);
        Assert.Equal(topMargin, welcome.Margin.Top);
        Assert.Equal(visible, rig.Conversation.FindByName<Border>("WelcomeHost").IsEffectivelyVisible);

        // 工具列、對話 ScrollViewer、輸入框都保留
        Assert.True(rig.Window.FindByName<ChatToolbar>("Toolbar").IsEffectivelyVisible);
        Assert.True(rig.Conversation.FindByName<ScrollViewer>("MessageScroller").IsEffectivelyVisible);
        Assert.True(rig.Composer.FindByName<TextBox>("InputBox").IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaTheory]
    [InlineData(1200, 800, 24.0)]
    [InlineData(800, 650, 16.0)]
    [InlineData(800, 560, 16.0)]
    public async Task MessageSpacing_Is24_AndBecomes16FromShort(double width, double height, double spacing)
    {
        var rig = new Rig(width, height);
        await rig.SendAsync();
        var stacks = rig.Conversation.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("conversation-stack")).ToList();
        Assert.True(stacks.Count >= 2);   // 外層 ConversationStack 與訊息清單
        Assert.All(stacks, p => Assert.Equal(spacing, p.Spacing));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ResizingFromNormalToVeryShortAndBack_TogglesTheWelcomePanelAndTheInputHeight()
    {
        var rig = new Rig(1200, 800);
        await rig.InitializeAsync();
        var welcome = rig.Conversation.FindByName<StackPanel>("WelcomePanel");
        var input = rig.Composer.FindByName<TextBox>("InputBox");
        Assert.True(welcome.IsEffectivelyVisible);
        Assert.Equal(64, input.MinHeight);

        Resize(rig.Window, 1200, 560);
        Assert.Contains("veryshort", rig.Window.Classes);
        Assert.False(welcome.IsEffectivelyVisible);
        Assert.Equal(48, input.MinHeight);

        Resize(rig.Window, 1200, 800);
        Assert.DoesNotContain("veryshort", rig.Window.Classes);
        Assert.DoesNotContain("short", rig.Window.Classes);
        Assert.True(welcome.IsEffectivelyVisible);
        Assert.Equal(64, input.MinHeight);
        rig.Window.Close();
    }

    private static void Resize(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    // ---- 可存取性：沒有文字的按鈕必須有可存取名稱 ----

    private static bool HasVisibleText(Control control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text));

    /// <summary>可見的按鈕（排除捲軸等範本內部的 RepeatButton）。</summary>
    private static List<ContentControl> VisibleButtons(Visual root) =>
        root.GetVisualDescendants().OfType<ContentControl>()
            .Where(c => c is Button or ToggleButton && c is not RepeatButton && c.IsEffectivelyVisible && !c.GetVisualAncestors().OfType<ScrollBar>().Any())
            .ToList();

    private static string Describe(Control c) => $"{c.GetType().Name}#{c.Name}";

    [AvaloniaTheory]
    [InlineData(1200, 800, false)]
    [InlineData(1200, 800, true)]
    [InlineData(800, 700, false)]
    [InlineData(800, 700, true)]
    [InlineData(520, 650, false)]
    [InlineData(520, 650, true)]
    [InlineData(520, 560, true)]
    public async Task EveryVisibleButtonWithoutText_HasAnAccessibleNameAndATooltip(double width, double height, bool withMessages)
    {
        var rig = new Rig(width, height);
        if (withMessages)
            await rig.SendAsync();
        else
            await rig.InitializeAsync();

        var buttons = VisibleButtons(rig.Window);
        Assert.NotEmpty(buttons);
        var iconOnly = buttons.Where(b => !HasVisibleText(b)).ToList();
        Assert.NotEmpty(iconOnly);   // 窄版的純圖示化按鈕一定在掃描範圍內

        foreach (var button in iconOnly)
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{Describe(button)} 缺少 AutomationProperties.Name");
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string), $"{Describe(button)} 缺少 ToolTip.Tip");
        }
        // 有文字的按鈕，文字本身就是名稱；仍然不能是空的 Content
        foreach (var button in buttons.Except(iconOnly))
            Assert.True(HasVisibleText(button) || button.Content is not null, Describe(button));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void TheToolbarMenuItems_HaveHeaders()
    {
        var rig = new Rig(1200, 800);
        var more = rig.Window.FindByName<Button>("MoreButton");
        var flyout = Assert.IsType<MenuFlyout>(more.Flyout);
        Assert.NotEmpty(flyout.Items);
        foreach (var item in flyout.Items.OfType<MenuItem>())
            Assert.False(string.IsNullOrWhiteSpace(item.Header as string), Describe(item));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ActionBarButtons_AreAllNamedAndWithTooltips()
    {
        var rig = new Rig(1200, 800);
        await rig.SendAsync();
        var actions = rig.Conversation.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "MessageActions" && p.IsEffectivelyVisible);
        var buttons = actions.Children.OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
        Assert.True(buttons.Count >= 2);   // 複製、重新產生
        foreach (var button in buttons)
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), Describe(button));
            Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string), Describe(button));
        }
        rig.Window.Close();
    }

    // ---- Tab 順序 ----

    private static bool IsTabStop(InputElement e) =>
        e.Focusable && KeyboardNavigation.GetIsTabStop(e) && e.IsEffectivelyEnabled && e.IsEffectivelyVisible;

    /// <summary>從標題列第一個控制項開始，一路按 Tab 會依序經過的控制項（回到起點或離開視窗為止）。</summary>
    private static List<InputElement> TabChain(WorkbenchWindow window)
    {
        var start = window.FindByName<Button>("SpriteModeButton");
        var chain = new List<InputElement> { start };
        IInputElement current = start;
        for (var i = 0; i < 100; i++)
        {
            var next = KeyboardNavigationHandler.GetNext(current, NavigationDirection.Next) as InputElement;
            if (next is null || chain.Contains(next))
                break;
            chain.Add(next);
            current = next;
        }
        return chain;
    }

    [AvaloniaTheory]
    [InlineData(1200, 800)]
    [InlineData(800, 700)]
    [InlineData(520, 650)]
    public async Task TabOrder_FollowsTheVisualOrder_AndSkipsTheDisabledControls(double width, double height)
    {
        var rig = new Rig(width, height);
        await rig.SendAsync();
        rig.Vm.InputText = "下一則";   // 送出鈕要有內容才可用
        var window = rig.Window;
        window.UpdateLayout();

        var chain = TabChain(window);
        int Index(string name) => chain.FindIndex(e => e is Control c && c.Name == name);

        var order = new[] { "SpriteModeButton", "NewConversationButton", "HistoryButton", "CapsuleButton", "MoreButton", "InputBox", "AddFileButton", "AddFolderButton", "SendButton" };
        var indexes = order.Select(Index).ToList();
        Assert.DoesNotContain(-1, indexes);
        Assert.Equal(indexes.OrderBy(i => i), indexes);   // 嚴格遞增：標題列 → 工具列 → 輸入區

        // 對話區內可聚焦的動作列按鈕在工具列之後、輸入框之前
        var actionButtons = chain.OfType<Button>().Where(b => b.GetVisualAncestors().OfType<StackPanel>().Any(p => p.Name == "MessageActions")).ToList();
        Assert.NotEmpty(actionButtons);
        Assert.All(actionButtons, b =>
        {
            var i = chain.IndexOf(b);
            Assert.InRange(i, Index("MoreButton") + 1, Index("InputBox") - 1);
        });

        // 歷史抽屜收起時，裡面的清單不在 Tab 鏈上
        Assert.DoesNotContain(chain, e => e is ListBox or ListBoxItem);

        // 停用的麥克風、👍👎 不在 Tab 鏈上
        Assert.Equal(-1, Index("MicButton"));
        Assert.Equal(-1, Index("LikeButton"));
        Assert.Equal(-1, Index("DislikeButton"));
        Assert.All(chain.Skip(1), e => Assert.True(e.IsEffectivelyEnabled && e.IsEffectivelyVisible));

        // 視覺樹的順序也一致（沒有靠 TabIndex 硬調）
        var visual = window.GetVisualDescendants().OfType<InputElement>().Where(IsTabStop).ToList();
        Assert.True(visual.FindIndex(e => e is Control { Name: "SpriteModeButton" }) < visual.FindIndex(e => e is Control { Name: "NewConversationButton" }));
        Assert.True(visual.FindIndex(e => e is Control { Name: "MoreButton" }) < visual.FindIndex(e => e is Control { Name: "InputBox" }));
        Assert.True(visual.FindIndex(e => e is Control { Name: "InputBox" }) < visual.FindIndex(e => e is Control { Name: "SendButton" }));
        window.Close();
    }

    [AvaloniaFact]
    public async Task WhileResponding_TheStopButtonReplacesSend_InTheTabChain_AndTheCapsuleAndSpriteAreSkipped()
    {
        var rig = new Rig(1200, 800);
        await rig.InitializeAsync();
        rig.Vm.IsResponding = true;
        rig.Window.UpdateLayout();

        var names = TabChain(rig.Window).OfType<Control>().Select(c => c.Name).ToList();
        Assert.Contains("StopButton", names);
        Assert.DoesNotContain("SendButton", names);
        Assert.DoesNotContain("CapsuleButton", names);   // 回應中停用
        Assert.DoesNotContain("AddFileButton", names);
        rig.Window.Close();
    }
}
