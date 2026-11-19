using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>輸入框上方的使用習慣提示列（plan/23 第 7.1 節）：按鈕數量與層級、有提示才佔版面。</summary>
public class ComposerHintTests
{
    private static ComposerHintContent Content(string? secondary = "繼續") => new(
        Text: "提示文字",
        PrimaryText: "開新對話",
        Primary: () => Task.CompletedTask,
        SecondaryText: secondary,
        Secondary: secondary is null ? null : () => Task.CompletedTask,
        Dismiss: () => Task.CompletedTask,
        Mute: () => Task.CompletedTask);

    private static (ComposerHint View, Window Host) Host(ComposerHintViewModel vm)
    {
        var view = new ComposerHint { DataContext = vm };
        var host = new Window { Content = view, Width = 480, Height = 300 };
        host.Show();
        host.UpdateLayout();
        return (view, host);
    }

    private static Button Named(Window host, string name) =>
        host.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void WithTwoActions_ShowsBothActions_AndTheTwoTertiaryButtons()
    {
        var vm = new ComposerHintViewModel();
        vm.Show(Content());
        var (_, host) = Host(vm);

        Assert.True(Named(host, "HintPrimaryButton").IsVisible);
        Assert.True(Named(host, "HintSecondaryButton").IsVisible);
        Assert.True(Named(host, "HintDismissButton").IsVisible);
        Assert.True(Named(host, "HintMuteButton").IsVisible);
        Assert.Contains("secondary", Named(host, "HintPrimaryButton").Classes);
        Assert.Contains("secondary", Named(host, "HintSecondaryButton").Classes);
        Assert.Contains("tertiary", Named(host, "HintDismissButton").Classes);
        Assert.Contains("tertiary", Named(host, "HintMuteButton").Classes);
        Assert.DoesNotContain("small", Named(host, "HintMuteButton").Classes);
        host.Close();
    }

    [AvaloniaFact]
    public void FourButtons_ShareOneRow_SameHeightFontSizeAndTop()
    {
        // ui-guide 第 06 節：同一列 Secondary 與 Tertiary 都是 32 高、預設字級，只靠顏色與框線區分
        var vm = new ComposerHintViewModel();
        vm.Show(Content());
        var (_, host) = Host(vm);

        var names = new[] { "HintPrimaryButton", "HintSecondaryButton", "HintDismissButton", "HintMuteButton" };
        var buttons = names.Select(n => Named(host, n)).ToArray();
        var tops = buttons.Select(b => b.TranslatePoint(new Point(0, 0), host)!.Value.Y).ToArray();

        Assert.All(buttons, b => Assert.Equal(32, b.Height));
        Assert.Single(buttons.Select(b => b.FontSize).Distinct());
        Assert.Single(tops.Distinct());
        host.Close();
    }

    [AvaloniaFact]
    public void WithoutSecondaryAction_HidesIt()
    {
        var vm = new ComposerHintViewModel();
        vm.Show(Content(secondary: null));
        var (_, host) = Host(vm);

        Assert.True(Named(host, "HintPrimaryButton").IsVisible);
        Assert.False(Named(host, "HintSecondaryButton").IsVisible);
        host.Close();
    }

    [AvaloniaFact]
    public void ComposerHintHost_IsHiddenUntilAHintExists_AndDoesNotTakeSpace()
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

        var host = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ComposerHintHost");
        Assert.False(host.IsVisible);

        vm.Hint.Show(Content());
        window.UpdateLayout();
        Assert.True(host.IsVisible);
        window.Close();
    }
}
