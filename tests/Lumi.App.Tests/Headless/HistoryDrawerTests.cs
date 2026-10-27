using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// 9B：歷史抽屜的視圖行為（headless）：無障礙名稱與 Tooltip、Tab 停止點、群組標題不能選取、鍵盤（Esc、F2、Enter／Esc、Delete、Shift+F10）、
/// 選單項目、載入更多與空狀態。
/// </summary>
public class HistoryDrawerTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public FakeConfirmService Confirm { get; } = new() { Answer = false };
        public SpriteConversationService Sprite { get; }
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }

        /// <summary>先建立資料，再開視窗（視窗開啟時會載入最近的對話；之後才塞資料，目前的對話就不對了）。</summary>
        public static async Task<Rig> CreateAsync(int conversations = 0, string prefix = "對話", bool withSpriteConversation = false)
        {
            var rig = new Rig(seeded: false);
            if (withSpriteConversation)
            {
                // 小精靈的專屬對話（M22 22J）：最舊，仍然固定在清單最上方
                var sprite = await rig.Sprite.GetOrCreateAsync(Strings.Sprite_ConversationTitle);
                await rig.Store.AppendMessageAsync(sprite.Id,
                    new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new Lumi.Core.Chat.TextBlock("小精靈你好")], DateTimeOffset.UtcNow));
                await Task.Delay(3);
            }
            await rig.SeedAsync(conversations, prefix);
            rig.ShowWindow();
            return rig;
        }

        private Rig(bool seeded = true)
        {
            var catalog = new FakeModelCatalog();
            Sprite = new SpriteConversationService(Store);
            var service = new ConversationService(new SequenceChatProvider(), Store, catalog);
            Vm = new WorkbenchViewModel(service, Store, catalog,
                new FakeApiKeyPrompt(false), new FakeSettingsWindowService(), new ConnectionStatusCache(), new FakeClipboardService(),
                new SyncUiDispatcher(), new FakeTimeProvider(), NullLogger<WorkbenchViewModel>.Instance,
                deletion: HistoryTestSupport.Deletion(Store), confirm: Confirm,
                session: new ChatSession(service, Store, sprite: Sprite), spriteConversation: Sprite);
            Window = new WorkbenchWindow(Vm);
            if (seeded)
                ShowWindow();
        }

        private void ShowWindow()
        {
            Window.Show();
            Window.UpdateLayout();
        }

        public HistoryDrawer Drawer => Window.FindByName<HistoryDrawer>("HistoryDrawerView");

        public ListBox List => Drawer.GetVisualDescendants().OfType<ListBox>().Single();

        public async Task SeedAsync(int count, string prefix = "對話")
        {
            for (var i = 0; i < count; i++)
            {
                var conversation = await Store.CreateAsync($"{prefix} {i}");
                await Store.AppendMessageAsync(conversation.Id,
                    new ChatMessage(ChatMessage.NewId(), ChatRole.User, [new Lumi.Core.Chat.TextBlock($"{prefix} {i}")], DateTimeOffset.UtcNow));
            }
        }

        /// <summary>像使用者一樣打開抽屜，等清單載入、版面排好。</summary>
        public async Task OpenAsync()
        {
            await Vm.InitializeAsync();
            await Pump(() => Vm.Title != Strings.Workbench_Title);   // 視窗的 Opened 也會載入（非同步），等它完成才有「目前的對話」
            var expected = Math.Min((await Store.ListAsync(1000)).Count, HistoryOptions.Default.PageSize);
            Vm.History.IsOpen = true;
            await Pump(() => Vm.History.Items.Count == expected);
            // 清單是虛擬化的：只有可見的列會實體化，等到第一列出現就好
            await Pump(() => expected == 0 || List.GetVisualDescendants().OfType<ListBoxItem>().Any(i => i.DataContext is ConversationListItem));
        }

        public async Task Pump(Func<bool> condition)
        {
            for (var i = 0; i < 300 && !condition(); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10);
            }
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Assert.True(condition());
        }

        public ListBoxItem RowFor(ConversationListItem item) =>
            List.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, item));

        /// <summary>F2 進入改名，等列realize 出改名輸入框（列是虛擬化的，要等版面排好）。</summary>
        public async Task<TextBox> StartRenameAsync(ConversationListItem item)
        {
            KeyDown(RowFor(item), Key.F2);
            await Pump(() => item.IsEditing);
            await Pump(() => EditorFor(item) is not null);
            return EditorFor(item)!;
        }

        public TextBox? EditorFor(ConversationListItem item) =>
            List.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => ReferenceEquals(t.DataContext, item) && t.IsEffectivelyVisible);

        public IEnumerable<Button> RowMoreButtons => Drawer.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("row-more"));
    }

    private static void KeyDown(Interactive target, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });

    [AvaloniaFact]
    public async Task RowMoreButtons_AreNamed_HaveATooltip_AndAreNotTabStops()
    {
        var rig = await Rig.CreateAsync(2);
        await rig.OpenAsync();

        await rig.Pump(() => rig.RowMoreButtons.Count() == 2);
        var buttons = rig.RowMoreButtons.ToList();
        Assert.Equal(2, buttons.Count);
        foreach (var button in buttons)
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.Equal(Strings.History_ItemMore, ToolTip.GetTip(button));
            Assert.False(button.IsTabStop);
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task GroupHeaders_CannotBeSelected_ButConversationRowsCan()
    {
        var rig = await Rig.CreateAsync(2);
        await rig.OpenAsync();

        var containers = rig.List.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.DataContext is not null).ToList();
        var header = Assert.Single(containers, c => c.DataContext is HistoryGroupHeader);
        Assert.False(header.IsEnabled);
        Assert.All(containers.Where(c => c.DataContext is ConversationListItem), c => Assert.True(c.IsEnabled));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Escape_InTheSearchBox_ClearsTheSearch()
    {
        var rig = await Rig.CreateAsync(2);
        await rig.OpenAsync();
        var box = rig.Window.FindByName<TextBox>("SearchBox");
        rig.Vm.History.SearchText = "對話";

        KeyDown(box, Key.Escape);

        Assert.Equal("", rig.Vm.History.SearchText);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task SearchBox_HasAName_AndTheClearButtonIsOnlyThereWhileTyping()
    {
        var rig = await Rig.CreateAsync(1);
        await rig.OpenAsync();
        var box = rig.Window.FindByName<TextBox>("SearchBox");
        var clear = rig.Window.FindByName<Button>("ClearSearchButton");

        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(box)));
        Assert.False(clear.IsEffectivelyVisible);
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(clear)));
        Assert.Equal(Strings.History_ClearSearch, ToolTip.GetTip(clear));

        rig.Vm.History.SearchText = "對";
        rig.Window.UpdateLayout();
        Assert.True(clear.IsEffectivelyVisible);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task NoResults_ShowsTheEmptyText_WithTheQuery()
    {
        var rig = await Rig.CreateAsync(1);
        await rig.OpenAsync();

        rig.Vm.History.SearchText = "完全不存在";
        await rig.Pump(() => rig.Vm.History.ShowNoResults);

        var text = rig.Drawer.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Single(t => t.Text == rig.Vm.History.NoResultsText);
        Assert.True(text.IsEffectivelyVisible);
        Assert.Equal(string.Format(Strings.History_NoResultsFormat, "完全不存在"), text.Text);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task LoadMoreButton_IsOnlyShownWhenThereIsAnotherPage()
    {
        var rig = await Rig.CreateAsync(3);
        await rig.OpenAsync();
        var more = rig.Drawer.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.History_LoadMore));
        Assert.False(more.IsEffectivelyVisible);
        rig.Window.Close();

        var big = await Rig.CreateAsync(55);
        await big.OpenAsync();
        big.Window.UpdateLayout();
        var bigMore = big.Drawer.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Strings.History_LoadMore));
        Assert.True(bigMore.IsEffectivelyVisible);
        Assert.True(bigMore.IsEffectivelyVisible);
        Assert.Contains("secondary", bigMore.Classes);   // 按鈕層級（ButtonCoverageTests）
        big.Window.Close();
    }

    [AvaloniaFact]
    public async Task F2OnARow_StartsRename_AndEnterCommitsIt()
    {
        var rig = await Rig.CreateAsync(1, "舊");
        await rig.OpenAsync();
        var item = rig.Vm.History.Items[0];

        var editor = await rig.StartRenameAsync(item);
        Assert.Equal(60, editor.MaxLength);

        editor.Text = "新名字";
        KeyDown(editor, Key.Enter);
        await rig.Pump(() => !item.IsEditing);

        Assert.Equal("新名字", item.Title);
        Assert.Equal("新名字", (await rig.Store.ListAsync(10)).Single().Title);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task EscapeInTheRenameEditor_CancelsWithoutSaving()
    {
        var rig = await Rig.CreateAsync(1, "保留");
        await rig.OpenAsync();
        var item = rig.Vm.History.Items[0];

        var editor = await rig.StartRenameAsync(item);
        editor.Text = "不要這個";
        KeyDown(editor, Key.Escape);

        Assert.False(item.IsEditing);
        Assert.Equal("保留 0", item.Title);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task RowMenu_HasRenameAndDeleteItems()
    {
        var rig = await Rig.CreateAsync(1);
        await rig.OpenAsync();

        var button = rig.RowMoreButtons.Single();
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        var items = menu.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { Strings.History_Rename, Strings.History_Delete, Strings.Workbench_ClearConversation },
            items.Select(i => i.Header as string).ToArray());

        // 開啟時才接上這一列的命令（Flyout 內容不繼承 DataContext）；一般對話沒有「清除內容」
        menu.ShowAt(button);
        Assert.Equal([Strings.History_Rename, Strings.History_Delete], items.Where(i => i.IsVisible).Select(i => i.Header as string));
        Assert.All(items.Where(i => i.IsVisible), i => Assert.NotNull(i.Command));
        Assert.Same(rig.Vm.History.Items[0], items[0].CommandParameter);
        Assert.Same(rig.Vm.History.Items[0], items[1].CommandParameter);
        menu.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task TheSpriteConversationRow_IsPinnedFirst_WithTheIconAndTag_AndItsMenuHasClearInsteadOfDelete()
    {
        var rig = await Rig.CreateAsync(2, withSpriteConversation: true);
        await rig.OpenAsync();
        var spriteItem = rig.Vm.History.Items[0];
        Assert.True(spriteItem.IsSprite);
        Assert.Same(spriteItem, rig.Vm.History.Rows[0]);

        await rig.Pump(() => rig.RowMoreButtons.Count() == 3);
        var row = rig.RowFor(spriteItem);
        // 標籤與小精靈圖示
        Assert.Contains(row.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
            t => t.Text == Strings.History_SpriteTag && t.IsEffectivelyVisible);
        Assert.Contains(row.GetVisualDescendants().OfType<Lumi.App.Controls.LumiIcon>(), i => i.IsEffectivelyVisible && i.Data is not null);
        // 一般列沒有標籤
        var ordinaryRow = rig.RowFor(rig.Vm.History.Items[1]);
        Assert.DoesNotContain(ordinaryRow.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
            t => t.Text == Strings.History_SpriteTag && t.IsEffectivelyVisible);

        var button = row.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("row-more"));
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        menu.ShowAt(button);
        var visible = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
        Assert.Equal([Strings.History_Rename, Strings.Workbench_ClearConversation], visible.Select(i => i.Header as string));
        Assert.Same(rig.Vm.History.ClearSpriteConversationCommand, visible[1].Command);
        menu.Hide();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task DeleteKeyOnTheSpriteConversationRow_DoesNothing()
    {
        var rig = await Rig.CreateAsync(1, withSpriteConversation: true);
        await rig.OpenAsync();
        var spriteItem = rig.Vm.History.Items[0];
        Assert.True(spriteItem.IsSprite);

        KeyDown(rig.RowFor(spriteItem), Key.Delete);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(rig.Confirm.Asked);
        Assert.NotNull(await rig.Store.GetConversationAsync(spriteItem.Id));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task DeleteKeyOnARow_AsksFirst_AndAnsweredNoKeepsIt()
    {
        var rig = await Rig.CreateAsync(1);
        await rig.OpenAsync();
        var item = rig.Vm.History.Items[0];

        KeyDown(rig.RowFor(item), Key.Delete);
        await rig.Pump(() => rig.Confirm.Asked.Count == 1);

        Assert.Contains(rig.Confirm.Asked, a => a.ConfirmText == Strings.Confirm_Delete && a.Message.Contains(item.Title));
        Assert.Single(await rig.Store.ListAsync(10));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task ShiftF10OnARow_OpensTheRowMenu()
    {
        var rig = await Rig.CreateAsync(1);
        await rig.OpenAsync();
        var item = rig.Vm.History.Items[0];
        var menu = Assert.IsType<MenuFlyout>(rig.RowMoreButtons.Single().Flyout);

        var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F10, KeyModifiers = KeyModifiers.Shift };
        rig.RowFor(item).RaiseEvent(key);

        Assert.True(key.Handled);   // 鍵盤已交給列的選單處理（headless 不一定真的畫出彈出層）
        Assert.NotNull(menu);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task DrawerIsCollapsed_NoRowIsReachableByTab()
    {
        var rig = await Rig.CreateAsync(2);
        await rig.OpenAsync();
        rig.Vm.History.IsOpen = false;
        rig.Window.UpdateLayout();

        Assert.False(rig.Drawer.IsEffectivelyVisible);
        Assert.All(rig.RowMoreButtons, b => Assert.False(b.IsEffectivelyVisible));
        rig.Window.Close();
    }
}
