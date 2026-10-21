using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Lumi.App.Controls;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.App.Sprites;
using Lumi.App.Tests.Sprites;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Lumi.App.Tests.Headless;

/// <summary>
/// UE-1：工具卡片、群組卡、權限確認、ConfirmDialog、ModelPicker、歷史抽屜、小精靈氣泡換 token、圖示與按鈕層級。
/// 每個確認區最多一個 primary（Destructive 是 danger）；狀態圖示是 LumiIcon（形狀不同），不再有 Unicode 符號。
/// </summary>
public class RemainingViewsStyleTests
{
    private sealed class Responder : IToolApprovalResponder
    {
        public void Resolve(string callId, ApprovalDecision decision) { }
    }

    private static ToolCardViewModel Card(string id = "c1", string name = "回聲") =>
        new(id, "echo", name, ToolPermission.Write, "檔案.txt", """{"text":"你好"}""", new Responder(),
            new FakeClipboardService(), new FakeTimeProvider());

    private static ToolResultBlock Ok(string id = "c1") => new(id, "完成了", false, "回聲", "檔案.txt", "182 行", 320, ToolApproval.NotRequired);

    private static T Show<T>(T view, object dataContext, double width = 700) where T : Control
    {
        view.DataContext = dataContext;
        new Window { Width = width, Height = 700, Content = view }.Show();
        ((Window)view.GetVisualRoot()!).UpdateLayout();
        return view;
    }

    private static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    private static readonly string[] BannedSymbols = ["✓", "✕", "⚠", "⊘", "↑", "■", "×", "▾", "⌄", "›", "✦", "✔"];

    private static void AssertNoSymbolText(Visual root)
    {
        foreach (var text in All<TextBlock>(root).Select(t => t.Text).Where(t => t is not null))
            Assert.DoesNotContain(BannedSymbols, symbol => text!.Contains(symbol));
        foreach (var button in All<Button>(root).Where(b => b.Content is string))
            Assert.DoesNotContain(BannedSymbols, symbol => ((string)button.Content!).Contains(symbol));
    }

    private static string Token(Control scope, string key)
    {
        Assert.True(scope.TryFindResource(key, scope.ActualThemeVariant, out var value), key);
        return ((ISolidColorBrush)value!).Color.ToString();
    }

    private static string ColorOf(IBrush? brush) => ((ISolidColorBrush)brush!).Color.ToString();

    private static IEnumerable<LumiIcon> VisibleIcons(Visual root) => All<LumiIcon>(root).Where(i => i.IsEffectivelyVisible);

    private static Geometry Icon(string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, null, out var value));
        return (Geometry)value!;
    }

    // ===== 工具卡片 =====

    [AvaloniaFact]
    public void ToolCard_IsACard_WithRadius14_AndNoPaddingOfItsOwn()
    {
        var view = Show(new ToolCardView(), Card());
        var border = view.FindByName<Border>("Card");

        Assert.Contains("card", border.Classes);
        Assert.Equal(new CornerRadius(14), border.CornerRadius);
        Assert.Equal(new Thickness(0), border.Padding);   // 內距由標題按鈕與內容區的 Margin 負責
        Assert.Equal(ColorOf(Application.Current!.Resources.TryGetResource("LumiSurfaceBrush", view.ActualThemeVariant, out var surface) ? (IBrush)surface! : null),
            ColorOf(border.Background));
    }

    [AvaloniaFact]
    public void ToolCard_StateIcons_AreLumiIcons_WithDistinctShapes_AndNoUnicodeSymbols()
    {
        var states = new List<(string Name, Action<ToolCardViewModel> Apply, string Icon)>
        {
            ("running", _ => { }, "LumiIconLoader"),
            ("succeeded", c => c.Complete(Ok()), "LumiIconCheck"),
            ("failed", c => c.Complete(new ToolResultBlock("c1", "壞了", true, "回聲", null, "失敗", 5, ToolApproval.NotRequired)), "LumiIconX"),
            ("awaiting", c => c.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Write, ToolPreview.None, "{}")), "LumiIconAlertTriangle"),
        };
        foreach (var (name, apply, iconKey) in states)
        {
            var card = Card();
            apply(card);
            var view = Show(new ToolCardView(), card);
            var header = All<Button>(view).First(b => b.Classes.Contains("cardheader"));

            var statusIcon = VisibleIcons(header).First();
            Assert.True(ReferenceEquals(Icon(iconKey), statusIcon.Data), $"{name} 的狀態圖示應該是 {iconKey}");
            AssertNoSymbolText(view);
        }
    }

    [AvaloniaFact]
    public void ToolCard_RunningIcon_Spins_OnlyWhileRunning()
    {
        var card = Card();
        var view = Show(new ToolCardView(), card);
        var spinner = All<LumiIcon>(view).Single(i => ReferenceEquals(i.Data, Icon("LumiIconLoader")));
        Assert.Contains("spin", spinner.Classes);

        card.Complete(Ok());
        Assert.DoesNotContain("spin", spinner.Classes);
    }

    [AvaloniaFact]
    public void ToolCard_DeniedCard_ShowsTheBanIcon_AndCollapseChevronFollowsTheState()
    {
        var card = Card();
        card.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Write, ToolPreview.None, "{}"));
        card.DenyCommand.Execute(null);
        var view = Show(new ToolCardView(), card);
        var header = All<Button>(view).First(b => b.Classes.Contains("cardheader"));
        Assert.Contains(VisibleIcons(header), i => ReferenceEquals(i.Data, Icon("LumiIconBan")));

        var done = Card("c2");
        done.Complete(Ok("c2"));
        var doneView = Show(new ToolCardView(), done);
        var doneHeader = All<Button>(doneView).First(b => b.Classes.Contains("cardheader"));
        Assert.Contains(VisibleIcons(doneHeader), i => ReferenceEquals(i.Data, Icon("LumiIconChevronRight")));   // 收合
        done.ToggleCommand.Execute(null);
        doneView.UpdateLayout();
        Assert.Contains(VisibleIcons(doneHeader), i => ReferenceEquals(i.Data, Icon("LumiIconChevronDown")));    // 展開
    }

    [AvaloniaFact]
    public void ToolCard_WriteConfirmation_HasExactlyOnePrimary_AndTheOthersAreSecondaryAndTertiary()
    {
        var card = Card();
        card.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Write, new ToolPreview("檔案.txt", "將寫入 3 行"), "{}"));
        var view = Show(new ToolCardView(), card);

        var allowOnce = view.FindByName<Button>("AllowOnceButton");
        var allowConversation = view.FindByName<Button>("AllowConversationButton");
        var deny = view.FindByName<Button>("DenyButton");
        Assert.Contains("primary", allowOnce.Classes);
        Assert.Contains("secondary", allowConversation.Classes);
        Assert.Contains("tertiary", deny.Classes);
        Assert.DoesNotContain("secondary", deny.Classes);

        var visible = new[] { "AllowOnceButton", "AllowConversationButton", "AllowExecuteButton", "DenyButton" }
            .Select(view.FindByName<Button>).Where(b => b.IsEffectivelyVisible).ToList();
        Assert.Single(visible, b => b.Classes.Contains("primary"));
        Assert.DoesNotContain(visible, b => b.Classes.Contains("danger"));
        Assert.Equal(Strings.Tool_AllowOnce, allowOnce.Content);
        Assert.Equal(Strings.Tool_AllowConversation, allowConversation.Content);
        Assert.Equal(Strings.Tool_Deny, deny.Content);
    }

    [AvaloniaFact]
    public void ToolCard_DestructiveConfirmation_UsesADangerOutlineButton_AndNoPrimary()
    {
        var card = Card();
        card.SetAwaiting(new ToolApprovalRequest("c1", "echo", "回聲", ToolPermission.Destructive, new ToolPreview("a.md", "將永久刪除"), "{}"));
        var view = Show(new ToolCardView(), card);

        var execute = view.FindByName<Button>("AllowExecuteButton");
        var deny = view.FindByName<Button>("DenyButton");
        Assert.True(execute.IsEffectivelyVisible);
        Assert.Contains("danger", execute.Classes);
        Assert.DoesNotContain("primary", execute.Classes);
        Assert.Equal(Strings.Tool_AllowExecute, execute.Content);
        // 紅框紅字：不再是整塊 Danger 底色
        Assert.Equal(Token(view, "LumiDangerBrush"), ColorOf(execute.Foreground));
        Assert.Equal(Token(view, "LumiSurfaceBrush"), ColorOf(execute.Background));
        Assert.Contains("secondary", deny.Classes);
        Assert.DoesNotContain("tertiary", deny.Classes);
        Assert.DoesNotContain(new[] { "AllowOnceButton", "AllowConversationButton", "AllowExecuteButton", "DenyButton" }
            .Select(view.FindByName<Button>).Where(b => b.IsEffectivelyVisible), b => b.Classes.Contains("primary"));
    }

    [AvaloniaFact]
    public void ToolCard_UsesTokenFontSizes_AndSpacingOnTheScale()
    {
        var card = Card();
        card.Complete(Ok());
        card.ToggleCommand.Execute(null);
        var view = Show(new ToolCardView(), card);

        var title = All<EmojiTextBlock>(view).First(t => t.Text == card.Title);
        Assert.Equal(14, title.FontSize);
        var result = view.FindByName<EmojiTextBlock>("ResultText");
        Assert.Equal(11, result.FontSize);
        var scale = new[] { 0d, 4, 8, 12, 16, 24, 32 };
        // Markdown 內容（MarkdownView）的間距在 C# 裡，不屬於這張卡片的版面
        foreach (var stack in All<StackPanel>(view).Where(s => !s.GetVisualAncestors().OfType<Lumi.App.Markdown.MarkdownView>().Any()))
            Assert.True(scale.Contains(stack.Spacing), $"{stack.Name ?? stack.Parent?.GetType().Name} Spacing={stack.Spacing}");
    }

    // ===== 群組卡 =====

    [AvaloniaFact]
    public void ToolGroup_IsACard_WithLumiIconStatus_AndChevron()
    {
        var run = new ToolRunViewModel();
        for (var i = 0; i < 3; i++)
        {
            var card = Card($"c{i}");
            card.Complete(Ok($"c{i}"));
            run.Cards.Add(card);
        }
        var view = Show(new ToolRunView(), run);
        var group = All<Border>(view).Single(b => b.Classes.Contains("toolgroup"));
        var header = view.FindByName<Button>("GroupHeader");

        Assert.Contains("card", group.Classes);
        Assert.Equal(new CornerRadius(14), group.CornerRadius);
        Assert.Contains(VisibleIcons(header), i => ReferenceEquals(i.Data, Icon("LumiIconCheck")));
        Assert.Contains(VisibleIcons(header), i => ReferenceEquals(i.Data, Icon("LumiIconChevronRight")));
        AssertNoSymbolText(view);

        run.ToggleGroupCommand.Execute(null);
        view.UpdateLayout();
        Assert.Contains(VisibleIcons(header), i => ReferenceEquals(i.Data, Icon("LumiIconChevronDown")));
    }

    // ===== ConfirmDialog =====

    [AvaloniaFact]
    public void ConfirmDialog_NormalConfirmIsPrimary_DestructiveIsDanger_CancelIsSecondary()
    {
        var normal = new ConfirmDialog("要繼續嗎？", "繼續");
        normal.Show();
        normal.UpdateLayout();
        Assert.Contains("primary", normal.ConfirmButton.Classes);
        Assert.DoesNotContain("danger", normal.ConfirmButton.Classes);
        Assert.Contains("secondary", normal.CancelButton.Classes);
        Assert.Equal("繼續", normal.ConfirmButton.Content);
        normal.Close();

        var destructive = new ConfirmDialog("要刪除嗎？", Strings.Confirm_Delete, destructive: true);
        destructive.Show();
        destructive.UpdateLayout();
        Assert.Contains("danger", destructive.ConfirmButton.Classes);
        Assert.DoesNotContain("primary", destructive.ConfirmButton.Classes);
        Assert.Equal(Token(destructive, "LumiDangerBrush"), ColorOf(destructive.ConfirmButton.Foreground));
        Assert.True(destructive.ConfirmButton.IsDefault);
        Assert.True(destructive.CancelButton.IsCancel);
        destructive.Close();
    }

    [AvaloniaFact]
    public void ConfirmDialog_UsesScaleSpacing()
    {
        var dialog = new ConfirmDialog("訊息", "確定");
        dialog.Show();
        dialog.UpdateLayout();
        var root = (StackPanel)dialog.Content!;
        Assert.Equal(new Thickness(24), root.Margin);
        Assert.Equal(24, root.Spacing);
        Assert.Equal(8, ((StackPanel)dialog.CancelButton.Parent!).Spacing);
        dialog.Close();
    }

    [AvaloniaFact]
    public void ConfirmService_Destructive_DefaultsToConfirmAsync_ForFakes()
    {
        IConfirmService fake = new FakeConfirmService();
        Assert.True(fake.ConfirmDestructiveAsync("要刪除嗎？", "刪除").GetAwaiter().GetResult());
        Assert.Equal([("要刪除嗎？", "刪除")], ((FakeConfirmService)fake).Asked);
    }

    // ===== ModelPicker =====

    private sealed class WorkbenchRig
    {
        public WorkbenchViewModel Vm { get; }
        public WorkbenchWindow Window { get; }

        public WorkbenchRig()
        {
            var store = new InMemoryConversationStore();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), store, catalog);
            Vm = new WorkbenchViewModel(service, store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), new FakeTimeProvider(),
                NullLogger<WorkbenchViewModel>.Instance);
            Window = new WorkbenchWindow(Vm);
            Window.Show();
            Window.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public void ModelPicker_CurrentModel_IsSoftWithOnSoftText_AndACheckIcon_NoUnicode()
    {
        var rig = new WorkbenchRig();
        var picker = new ModelPicker { DataContext = rig.Vm.ModelSwitcher };
        var host = new Window { Content = picker, Width = 400, Height = 600 };
        host.Show();
        host.UpdateLayout();

        var current = All<Border>(picker).Single(b => b.Classes.Contains("model-row") && b.Classes.Contains("current"));
        Assert.Equal(Token(picker, "LumiAccentSoftBrush"), ColorOf(current.Background));
        Assert.Equal(new CornerRadius(10), current.CornerRadius);
        Assert.Contains(All<LumiIcon>(current), i => i.IsEffectivelyVisible && ReferenceEquals(i.Data, Icon("LumiIconCheck")));
        var title = All<TextBlock>(current).First(t => t.IsEffectivelyVisible);
        Assert.Equal(Token(picker, "LumiTextOnSoftBrush"), ColorOf(title.Foreground));
        AssertNoSymbolText(picker);
        host.Close();
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ModelPicker_ManageIsTertiary_AndSearchBoxKeepsTheGlobalTextBoxStyle()
    {
        var rig = new WorkbenchRig();
        var picker = new ModelPicker { DataContext = rig.Vm.ModelSwitcher };
        var host = new Window { Content = picker, Width = 400, Height = 600 };
        host.Show();
        host.UpdateLayout();

        var manage = All<Button>(picker).Single(b => (string?)b.Content == Strings.ModelPicker_Manage);
        Assert.Contains("tertiary", manage.Classes);
        Assert.DoesNotContain("primary", manage.Classes);
        var search = picker.FindByName<TextBox>("SearchBox");
        Assert.True(search.MinHeight >= 40);
        Assert.Equal(new CornerRadius(10), search.CornerRadius);
        host.Close();
        rig.Window.Close();
    }

    // ===== 歷史抽屜 =====

    [AvaloniaFact]
    public async Task HistoryDrawer_Items_HaveRadius10_Padding12x8_AndSoftSelection()
    {
        var rig = new WorkbenchRig();
        await rig.Vm.InitializeAsync();
        rig.Vm.InputText = "問";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        rig.Vm.History.ToggleCommand.Execute(null);
        rig.Window.UpdateLayout();

        var list = All<ListBox>(rig.Window).Single(l => l.Classes.Contains("history-list"));
        var item = All<ListBoxItem>(list).First();
        Assert.Equal(new CornerRadius(10), item.CornerRadius);
        Assert.Equal(new Thickness(12, 8), item.Padding);
        // 時間是 Caption
        var time = All<TextBlock>(item).Last();
        Assert.Equal(11, time.FontSize);

        item.IsSelected = true;
        rig.Window.UpdateLayout();
        var presenter = All<ContentPresenter>(item).First(p => p.Name == "PART_ContentPresenter");
        Assert.Equal(Token(rig.Window, "LumiAccentSoftBrush"), ColorOf(presenter.Background));
        Assert.Equal(Token(rig.Window, "LumiTextOnSoftBrush"), ColorOf(All<TextBlock>(item).First().Foreground));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void HistoryDrawer_EmptyState_ShowsACaptionMessage()
    {
        var rig = new WorkbenchRig();
        rig.Vm.History.ToggleCommand.Execute(null);
        rig.Window.UpdateLayout();

        var empty = All<TextBlock>(rig.Window).Single(t => t.Text == Strings.Workbench_HistoryEmpty);
        Assert.True(empty.IsEffectivelyVisible);
        Assert.Equal(11, empty.FontSize);
        rig.Window.Close();
    }

    // ===== 小精靈氣泡 =====

    private static (SpriteBubbleView View, SpriteBubbleViewModel Vm) Bubble()
    {
        var time = new FakeTimeProvider();
        var vm = new SpriteBubbleViewModel(new SpriteShellOptions(), time, new RecordingResponder());
        var view = new SpriteBubbleView { DataContext = vm };
        new Window { Width = 340, Height = 400, Content = view }.Show();
        return (view, vm);
    }

    private static void Refresh(Control view) => ((Window)view.GetVisualRoot()!).UpdateLayout();

    [AvaloniaFact]
    public void SpriteBubble_Approval_HasOnePrimary_AndDenyIsTertiary()
    {
        var (view, vm) = Bubble();
        vm.OnRunStarted();
        vm.OnEvent(Ev.Approval("c1"));
        Refresh(view);

        var allow = view.FindControl<Button>("AllowButton")!;
        var deny = view.FindControl<Button>("DenyButton")!;
        Assert.Contains("primary", allow.Classes);
        Assert.DoesNotContain("danger", allow.Classes);
        Assert.Contains("tertiary", deny.Classes);
        Assert.Equal(Token(view, "LumiPrimaryButtonBrush"), ColorOf(allow.Background));

        vm.ToggleMoreOptionsCommand.Execute(null);
        Refresh(view);
        var once = view.FindControl<Button>("OptionAllowOnce")!;
        var conversation = view.FindControl<Button>("OptionAllowConversation")!;
        var optionDeny = view.FindControl<Button>("OptionDeny")!;
        Assert.Contains("primary", once.Classes);
        Assert.Contains("secondary", conversation.Classes);
        Assert.Contains("tertiary", optionDeny.Classes);
        Assert.Single(new[] { once, conversation, optionDeny }, b => b.Classes.Contains("primary"));
        ((Window)view.GetVisualRoot()!).Close();
    }

    [AvaloniaFact]
    public void SpriteBubble_Destructive_UsesDangerForAllow_AndSecondaryForDeny()
    {
        var (view, vm) = Bubble();
        vm.OnRunStarted();
        vm.OnEvent(Ev.Approval("c1", "刪除筆記", "notes/a.md", ToolPermission.Destructive, detail: "notes/a.md"));
        Refresh(view);

        var allow = view.FindControl<Button>("AllowButton")!;
        var deny = view.FindControl<Button>("DenyButton")!;
        Assert.Contains("danger", allow.Classes);
        Assert.DoesNotContain("primary", allow.Classes);
        Assert.Equal(Strings.Sprite_AllowDestructive, allow.Content);
        Assert.Contains("secondary", deny.Classes);
        Assert.DoesNotContain("tertiary", deny.Classes);
        Assert.Equal(Token(view, "LumiDangerBrush"), ColorOf(allow.Foreground));
        ((Window)view.GetVisualRoot()!).Close();
    }

    [AvaloniaFact]
    public void SpriteBubble_Error_RetryIsPrimary_SettingsIsSecondary_AndNoUnicode()
    {
        var (view, vm) = Bubble();
        vm.OnRunStarted();
        vm.OnRunFinished(Ev.Finished(Ev.Failure(ChatFailureKind.Network, "連不上伺服器")));
        Refresh(view);

        Assert.Contains("primary", view.FindControl<Button>("RetryButton")!.Classes);
        Assert.Contains("secondary", view.FindControl<Button>("SettingsButton")!.Classes);
        AssertNoSymbolText(view);
        ((Window)view.GetVisualRoot()!).Close();
    }
}
