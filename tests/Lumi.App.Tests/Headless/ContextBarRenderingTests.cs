using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
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

/// <summary>4C：輸入區的上下文列、chip、按鈕與使用者氣泡上的 chip 實際渲染出來的樣子。</summary>
public class ContextBarRenderingTests
{
    private sealed class Rig
    {
        public InMemoryConversationStore Store { get; } = new();
        public InMemoryConversationContextStore Context { get; } = new();
        public FakeContextInspector Inspector { get; } = new();
        public FakeFilePicker Picker { get; } = new();
        public ContextBarViewModel Bar { get; }
        public WorkbenchViewModel Vm { get; }

        public Rig()
        {
            var time = new FakeTimeProvider();
            var builder = new FakeContextBuilder();
            var catalog = new FakeModelCatalog();
            var service = new ConversationService(new SequenceChatProvider(SequenceChatProvider.Answer("好")), Store, catalog, new ToolRegistry([]),
                new ToolApprovalService(), new ToolPermissionMemory(), new ToolLoopOptions(), time, NullLogger<ConversationService>.Instance,
                Context, new EmptyFileScopeFactory(), builder);
            Bar = new ContextBarViewModel(Context, Store, Inspector, builder, new HeuristicTokenEstimator(), Picker, new FakeClipboardImage(),
                new FakeAttachmentStore(), time);
            Vm = new WorkbenchViewModel(service, Store, catalog, new FakeApiKeyPrompt(false), new FakeSettingsWindowService(),
                new ConnectionStatusCache(), new FakeClipboardService(), new SyncUiDispatcher(), time, NullLogger<WorkbenchViewModel>.Instance,
                contextBar: Bar);
        }
    }

    private static async Task<WorkbenchWindow> ShowAsync(Rig rig)
    {
        var window = new WorkbenchWindow(rig.Vm);
        window.Show();
        await rig.Vm.InitializeAsync();
        window.UpdateLayout();
        return window;
    }

    private static IEnumerable<T> All<T>(Control root) where T : Control =>
        new[] { root }.Concat(root.GetLogicalDescendants().OfType<Control>()).OfType<T>();

    private static string TextOf(TextBlock block) =>
        block.Text ?? string.Concat(block.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []);

    private static IEnumerable<string> VisibleTexts(Control root) =>
        All<TextBlock>(root).Where(t => t.IsEffectivelyVisible).Select(TextOf);

    private static void Click(Window window, Button button)
    {
        window.UpdateLayout();
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task WithoutItems_TheContextBarIsHidden_ButTheAddButtonsAreThere()
    {
        var rig = new Rig();
        var window = await ShowAsync(rig);

        Assert.False(window.FindByName<StackPanel>("ContextBar").IsEffectivelyVisible);
        Assert.True(window.FindByName<Button>("AddFileButton").IsEffectivelyVisible);
        Assert.True(window.FindByName<Button>("AddFolderButton").IsEffectivelyVisible);
        Assert.True(window.FindByName<Border>("InputArea").GetValue(DragDrop.AllowDropProperty));
    }

    [AvaloniaFact]
    public async Task AddingItems_ShowsTheBarWithChips_DetailsAndTheTokenEstimate()
    {
        var rig = new Rig();
        rig.Inspector.Know("/proj", ContextKind.Folder, new ContextItemStatus(ContextItemProblem.None, null, 1234))
            .Know("/a.txt", ContextKind.File, new ContextItemStatus(ContextItemProblem.TooLarge, 500_000));
        var window = await ShowAsync(rig);

        await rig.Bar.AddPathsAsync(["/proj", "/a.txt"]);
        window.UpdateLayout();

        var bar = window.FindByName<StackPanel>("ContextBar");
        Assert.True(bar.IsEffectivelyVisible);
        var texts = VisibleTexts(bar).ToList();
        Assert.Contains("此對話的上下文", texts);
        Assert.Contains("proj", texts);
        Assert.Contains("1,234 個項目", texts);
        Assert.Contains("a.txt", texts);
        Assert.Contains("太大，由模型按需讀取", texts);
        Assert.Contains(texts, t => t.StartsWith("≈ ") && t.EndsWith("tokens"));
        // 資料夾 chip 是琥珀淡底、有問題的 chip 是警告色
        var chips = All<Border>(window.FindByName<ItemsControl>("ContextChips")).Where(b => b.Classes.Contains("chip")).ToList();
        Assert.Equal(2, chips.Count);
        Assert.Contains("folder", chips[0].Classes);
        Assert.Contains("problem", chips[1].Classes);
    }

    [AvaloniaFact]
    public async Task ClickingTheRemoveButton_RemovesTheChip()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        var window = await ShowAsync(rig);
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        window.UpdateLayout();
        var remove = All<Button>(window.FindByName<ItemsControl>("ContextChips")).Single(b => b.Content as string == "×");

        Click(window, remove);
        await SettingsHost.WaitUntil(() => !rig.Bar.HasItems);
        window.UpdateLayout();

        Assert.False(window.FindByName<StackPanel>("ContextBar").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ClickingTheAddButtons_OpensThePickers()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File).Know("/proj", ContextKind.Folder);
        rig.Picker.Files.Add("/a.txt");
        rig.Picker.Folder = "/proj";
        var window = await ShowAsync(rig);

        Click(window, window.FindByName<Button>("AddFileButton"));
        await SettingsHost.WaitUntil(() => rig.Picker.FilePicks == 1 && rig.Bar.Items.Count == 1);
        Click(window, window.FindByName<Button>("AddFolderButton"));
        await SettingsHost.WaitUntil(() => rig.Picker.FolderPicks == 1 && rig.Bar.Items.Count == 2);

        Assert.Equal(["a.txt", "proj"], rig.Bar.Items.Select(c => c.DisplayName));
    }

    [AvaloniaFact]
    public async Task WhileLocked_TheAddButtonsAreDisabled_AndAHintIsShown()
    {
        var rig = new Rig();
        var window = await ShowAsync(rig);

        rig.Bar.IsLocked = true;
        rig.Vm.IsResponding = true;
        window.UpdateLayout();

        Assert.False(window.FindByName<Button>("AddFileButton").IsEffectivelyEnabled);
        Assert.False(window.FindByName<Button>("AddFolderButton").IsEffectivelyEnabled);
        Assert.Contains("回應中不能更改上下文", VisibleTexts(window));
    }

    [AvaloniaFact]
    public async Task ProblemsAreShown_NoticeAndErrorLines()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        var window = await ShowAsync(rig);
        await rig.Bar.AddPathsAsync(["/a.txt", "/gone.md"]);
        window.UpdateLayout();

        Assert.Contains("找不到：gone.md", VisibleTexts(window.FindByName<StackPanel>("ContextBar")));
    }

    [AvaloniaFact]
    public async Task OverTheContextWindow_ShowsAWarningMark()
    {
        var rig = new Rig();
        rig.Inspector.Know("/a.txt", ContextKind.File);
        var window = await ShowAsync(rig);
        await rig.Bar.AddPathsAsync(["/a.txt"]);
        rig.Bar.ContextWindow = 10;
        window.UpdateLayout();

        Assert.Contains("⚠", VisibleTexts(window.FindByName<StackPanel>("ContextBar")));
    }

    [AvaloniaFact]
    public async Task UserBubble_ShowsTheChipsItCarried()
    {
        var rig = new Rig();
        rig.Inspector.Know("/proj", ContextKind.Folder).Know("/a.txt", ContextKind.File);
        var window = await ShowAsync(rig);
        await rig.Bar.AddPathsAsync(["/proj", "/a.txt"]);
        rig.Vm.InputText = "看一下";
        await rig.Vm.SendCommand.ExecuteAsync(null);
        window.UpdateLayout();

        var texts = VisibleTexts(window).ToList();
        Assert.Contains("看一下", texts);
        // 氣泡上的 chip（唯讀）：和上下文列裡的同名 chip 各一份
        Assert.Equal(2, texts.Count(t => t == "proj"));
        Assert.Equal(2, texts.Count(t => t == "a.txt"));
        Assert.Contains("已帶入", texts);   // 上下文列裡的 chip 變成已帶入
    }

    [Fact]
    public void ThumbnailConverter_ReturnsNull_ForMissingFilesAndNonPaths()
    {
        // 解碼失敗的情況（壞掉的圖片）靠真實的圖片解碼器：Headless 的替身解碼器對任何內容都回一張假圖，無法在這裡驗證
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Null(PathToBitmapConverter.Instance.Convert("/no/such/file.png", typeof(object), null, culture));
        Assert.Null(PathToBitmapConverter.Instance.Convert(null, typeof(object), null, culture));
        Assert.Null(PathToBitmapConverter.Instance.Convert(42, typeof(object), null, culture));
    }
}
