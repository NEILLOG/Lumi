using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Markdown;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>M36 E8「問這段」：選取助理回答的文字後出現浮動工具列、右鍵選單的「問這段」、只在可以開支線時出現。</summary>
public class AskSelectionTests
{
    private const string Answer = "前面**粗體**後面選取文字";

    private static (WorkbenchWindow Window, WorkbenchViewModel Vm, ConversationView View, MarkdownView Markdown) Show(bool canBranch = true)
    {
        var store = new InMemoryConversationStore();
        var catalog = new FakeModelCatalog();
        var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
        var vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
            new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
            NullLogger<WorkbenchViewModel>.Instance);
        var window = new WorkbenchWindow(vm) { Width = 1200, Height = 1000 };
        window.Show();

        var message = MessageViewModel.From(new ChatMessage("a1", ChatRole.Assistant, [new Lumi.Core.Chat.TextBlock(Answer)], DateTimeOffset.UtcNow));
        message.ShowBranchAction = true;
        message.CanBranch = canBranch;
        vm.Messages.Add(message);
        window.UpdateLayout();

        var view = window.FindByName<ConversationView>("Conversation");
        var markdown = window.GetVisualDescendants().OfType<MarkdownView>().Single();
        return (window, vm, view, markdown);
    }

    private static List<SelectableTextBlock> TextBlocks(MarkdownView view) =>
        view.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible).ToList();

    private static Point CenterOfText(Window window, MarkdownTextBlock block, int position)
    {
        var rect = block.TextLayout.HitTestTextPosition(position);
        return block.TranslatePoint(new Point(rect.X + 1, rect.Y + rect.Height / 2), window)!.Value;
    }

    /// <summary>按住左鍵從 from 拖到 to 再放開（同 MessageTextSelectionTests）。</summary>
    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
    }

    /// <summary>拖曳選取回答裡的前幾個字，放開滑鼠；回傳放開的位置。</summary>
    private static Point SelectFirstWords(Window window, MarkdownView markdown)
    {
        var block = (MarkdownTextBlock)TextBlocks(markdown).First();
        var from = CenterOfText(window, block, 0);
        var to = CenterOfText(window, block, 4);
        Drag(window, from, to);
        return to;
    }

    [AvaloniaFact]
    public void Popup_opens_after_selection_in_branchable_message()
    {
        var (window, _, view, markdown) = Show();

        SelectFirstWords(window, markdown);

        Assert.False(string.IsNullOrEmpty(markdown.SelectedText));
        Assert.True(view.AskPopup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void No_popup_in_user_message_or_when_not_branchable()
    {
        var (window, _, view, markdown) = Show(canBranch: false);

        SelectFirstWords(window, markdown);

        Assert.False(string.IsNullOrEmpty(markdown.SelectedText));
        Assert.False(view.AskPopup.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ask_button_runs_the_command_and_closes_the_popup()
    {
        var (window, vm, view, markdown) = Show();
        SelectFirstWords(window, markdown);
        Assert.True(view.AskPopup.IsOpen);

        view.AskButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.False(view.AskPopup.IsOpen);
        Assert.NotNull(vm.AskAboutSelectionCommand.ExecutionTask);
        await Task.Delay(10);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Popup_copy_copies_the_selection_as_markdown()
    {
        var (window, _, view, markdown) = Show();
        SelectFirstWords(window, markdown);

        view.AskCopyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);

        Assert.Equal("前面**粗體**", await window.Clipboard!.TryGetTextAsync());
        window.Close();
    }

    [AvaloniaFact]
    public void Context_menu_has_ask_only_with_selection()
    {
        var (window, _, _, markdown) = Show();
        var block = (MarkdownTextBlock)TextBlocks(markdown).First();
        // 沒有選取：選單裡沒有「問這段」（只剩「複製」）
        block.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = block });
        Assert.Null(block.ContextMenu!.Items.OfType<MenuItem>().SingleOrDefault(m => Equals(m.Header, Strings.Branch_Ask)));

        // 有選取：出現在「複製」下面
        block.SelectAll();
        block.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = block });
        var items = block.ContextMenu!.Items.OfType<MenuItem>().ToList();
        Assert.Equal([Strings.Workbench_Copy, Strings.Branch_Ask], items.Select(m => m.Header));
        window.Close();
    }
}
