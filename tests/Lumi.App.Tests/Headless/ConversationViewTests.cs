using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>UB-3：歡迎區、訊息呈現（頭像、時間、氣泡）、動作列（👍👎 停用並說明）。</summary>
public class ConversationViewTests
{
    private static (WorkbenchWindow Window, WorkbenchViewModel Vm, ConversationView View) Show()
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        var window = new WorkbenchWindow(vm);
        window.Show();
        window.UpdateLayout();
        return (window, vm, window.FindByName<ConversationView>("Conversation"));
    }

    private static async Task SendAsync(WorkbenchWindow window, WorkbenchViewModel vm, string text = "嗨")
    {
        vm.InputText = text;
        await vm.SendCommand.ExecuteAsync(null);
        window.UpdateLayout();
    }

    // 每則訊息的模板同時有使用者與助理兩側（用 IsVisible 切換），所以只看目前看得到的
    private static T Visible<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name && c.IsEffectivelyVisible);

    // ---- 歡迎區 ----

    [AvaloniaFact]
    public async Task WelcomePanel_ShowsTitleSubtitleAndIcon_UntilThereIsAMessage()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        window.UpdateLayout();

        var welcome = view.FindByName<StackPanel>("WelcomePanel");
        Assert.True(welcome.IsVisible);
        Assert.Contains(welcome.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Workbench_WelcomeTitle);
        var subtitle = view.FindByName<TextBlock>("WelcomeSubtitle");
        Assert.Equal(Strings.Workbench_WelcomeSubtitle, subtitle.Text);
        Assert.DoesNotContain("麥克風", subtitle.Text);   // 麥克風尚未支援，副標不提
        Assert.NotNull(welcome.GetVisualDescendants().OfType<LumiIcon>().SingleOrDefault());

        await SendAsync(window, vm);

        Assert.False(welcome.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task WelcomePanel_ReturnsForANewConversation()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);
        await vm.NewConversationCommand.ExecuteAsync(null);
        window.UpdateLayout();

        Assert.True(view.FindByName<StackPanel>("WelcomePanel").IsVisible);
        window.Close();
    }

    // ---- 訊息 ----

    [AvaloniaFact]
    public async Task UserMessage_ShowsNameAndTime_AboveTheBubble_AlignedRight()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);

        var user = vm.Messages[0];
        var header = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("sender") && t.Text == Strings.Workbench_You && t.IsEffectivelyVisible);
        Assert.True(header.IsEffectivelyVisible);
        var time = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == user.TimeText && t.IsEffectivelyVisible).ToList();
        Assert.Contains(time, t => ReferenceEquals(t.GetVisualParent(), header.GetVisualParent()));
        var row = (StackPanel)header.GetVisualParent()!;
        Assert.Equal(HorizontalAlignment.Right, row.HorizontalAlignment);

        var bubble = view.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Child is EmojiTextBlock { Text: "嗨" });
        Assert.Equal(new CornerRadius(14), bubble.CornerRadius);
        Assert.Equal(new Thickness(16, 12), bubble.Padding);
        Assert.Equal(HorizontalAlignment.Right, bubble.HorizontalAlignment);
        Assert.True(header.TranslatePoint(default, view)!.Value.Y < bubble.TranslatePoint(default, view)!.Value.Y);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AssistantMessage_HasAvatar_NameTime_AndTheSoftBubble()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);

        var assistant = vm.Messages.Single(m => m.IsAssistant);
        var avatar = view.GetVisualDescendants().OfType<Border>().Single(b => b.IsEffectivelyVisible && b.Classes.Contains("avatar"));
        Assert.Equal(32, avatar.Bounds.Width);
        Assert.Equal(32, avatar.Bounds.Height);
        Assert.Equal(16, avatar.CornerRadius.TopLeft);
        Assert.Equal(Strings.TitleBar_LogoMark, ((TextBlock)avatar.Child!).Text);

        var name = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("sender") && t.Text == Strings.Workbench_AssistantName && t.IsEffectivelyVisible);
        Assert.Contains(((StackPanel)name.GetVisualParent()!).Children.OfType<TextBlock>(), t => t.Text == assistant.TimeText);

        var bubble = Visible<Border>(view, "AssistantBubble");
        Assert.True(bubble.IsEffectivelyVisible);
        Assert.Equal(new CornerRadius(14), bubble.CornerRadius);
        Assert.Equal(new Thickness(16, 12), bubble.Padding);
        var app = Application.Current!;
        var expected = (ISolidColorBrush)app.FindResource(app.ActualThemeVariant, "LumiAssistantBubbleBrush")!;
        Assert.Equal(expected.Color, ((ISolidColorBrush)bubble.Background!).Color);
        Assert.Contains(bubble.GetVisualDescendants(), v => v is Lumi.App.Markdown.MarkdownView);
        Assert.Equal(820, ((Grid)avatar.GetVisualParent()!).MaxWidth);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ConversationLayout_UsesWideValues_InOneOverridablePlace()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);

        var page = view.FindByName<Border>("ConversationPage");
        Assert.Contains("conversation-page", page.Classes);
        Assert.Equal(new Thickness(64, 24), page.Padding);
        var stack = view.FindByName<StackPanel>("ConversationStack");
        Assert.Equal(24, stack.Spacing);
        var messages = view.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("conversation-stack")).ToList();
        Assert.All(messages, p => Assert.Equal(24, p.Spacing));

        // 之後 RWD（UC）只要覆寫 class 樣式，不需要改結構
        page.Classes.Add("compact");
        Assert.Contains("compact", page.Classes);
        window.Close();
    }

    // ---- 動作列 ----

    [AvaloniaFact]
    public async Task Actions_AreTertiaryButtonsWithIconsTooltipsAndAutomationNames()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);

        var actions = view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "MessageActions" && p.IsEffectivelyVisible);
        var buttons = actions.Children.OfType<Button>().Where(b => b.IsEffectivelyVisible || !b.IsEnabled).ToList();
        Assert.Equal(4, buttons.Count);   // 複製、重新產生（最後一則）、👍、👎
        Assert.All(buttons, b => Assert.Contains("tertiary", b.Classes));
        Assert.All(buttons, b => Assert.NotNull(b.GetVisualDescendants().OfType<LumiIcon>().FirstOrDefault()));
        Assert.All(buttons, b => Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(b) as string)));
        Assert.All(buttons, b => Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(b))));

        Assert.Same(vm.CopyCommand, buttons[0].Command);
        Assert.Same(vm.RegenerateCommand, buttons[1].Command);
        Assert.Contains(buttons[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Workbench_Copy);
        Assert.Contains(buttons[1].GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Strings.Workbench_Regenerate);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ThumbsButtons_AreDisabled_WithAVisibleCaptionAndTooltip()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);

        var like = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LikeButton" && b.IsEffectivelyVisible);
        var dislike = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DislikeButton" && b.IsEffectivelyVisible);
        foreach (var button in new[] { like, dislike })
        {
            Assert.False(button.IsEnabled);
            Assert.Null(button.Command);
            Assert.Equal(Strings.Workbench_FeedbackComingSoon, ToolTip.GetTip(button));
            Assert.True(ToolTip.GetShowOnDisabled(button));
        }
        Assert.Equal(Strings.Workbench_Like, AutomationProperties.GetName(like));
        Assert.Equal(Strings.Workbench_Dislike, AutomationProperties.GetName(dislike));

        // 旁邊有一個共用的可見說明（不是每個按鈕各一個）
        var captions = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text == Strings.Workbench_FeedbackComingSoon && t.IsEffectivelyVisible && t.Classes.Contains("muted")).ToList();
        Assert.Single(captions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Regenerate_IsOnlyOnTheLastAssistantMessage()
    {
        var (window, vm, view) = Show();
        await vm.InitializeAsync();
        await SendAsync(window, vm);
        window.UpdateLayout();

        var regenerate = view.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.Command, vm.RegenerateCommand)).ToList();
        Assert.Single(regenerate, b => b.IsEffectivelyVisible);
        window.Close();
    }
}
