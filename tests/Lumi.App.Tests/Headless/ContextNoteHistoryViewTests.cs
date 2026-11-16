using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests.Headless;

/// <summary>M35 35I：版本歷史的畫面（headless）：三種差異列都畫得出來（有文字徽章）、寬的時候清單在右、窄的時候清單在差異上方。</summary>
public class ContextNoteHistoryViewTests
{
    private const string NoteId = "note-1";
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static ContextNoteRevision Rev(int version, ContextNoteRevisionKind kind, string content) =>
        new(ContextNoteRevision.NewId(), NoteId, version, kind, "目標", content, [], null, 0, Start.AddMinutes(version));

    private static string Content(params string[] decided) =>
        "## " + ContextNoteDocument.Decided + "\n\n" + string.Join("\n\n", decided);

    /// <summary>第 2 版相對第 1 版：一段修改（Changed）、一段刪除（Removed）、一段新增（Added）。</summary>
    private static async Task<ContextNoteHistoryViewModel> LoadThreeKindsAsync()
    {
        var store = new InMemoryContextNoteStore();
        var first = Rev(1, ContextNoteRevisionKind.StartBlank, Content("我們決定先用 Ollama 接本機模型", "另一個要刪掉的決定"));
        await store.CreateAsync(new ContextNote(NoteId, "conv-1", "目標", true, 1, 0, null, null, Start, Start), first);
        await store.AddRevisionAsync(Rev(2, ContextNoteRevisionKind.UserEdit, Content("我們決定先用 Ollama 相容端點接本機模型", "新加的決定")));

        var vm = new ContextNoteHistoryViewModel(store, new FakeContextNoteService(), new FakeConfirmService());
        await vm.LoadAsync(NoteId);
        return vm;
    }

    private static Window Host(ContextNoteHistoryViewModel vm, double width)
    {
        var view = new ContextNoteHistoryView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = 600 };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static bool HasVisibleText(Visual root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == text);

    [AvaloniaFact]
    public async Task AllThreeKindsOfRows_RenderWithTheirTextBadges()
    {
        var vm = await LoadThreeKindsAsync();
        var window = Host(vm, 900);
        try
        {
            Assert.True(HasVisibleText(window, Strings.ContextNote_History_Changed));
            Assert.True(HasVisibleText(window, Strings.ContextNote_History_Removed));
            Assert.True(HasVisibleText(window, Strings.ContextNote_History_Added));
            Assert.True(HasVisibleText(window, vm.CompareText));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WideWindow_ListSitsBesideTheDiff()
    {
        var vm = await LoadThreeKindsAsync();
        var window = Host(vm, 900);
        try
        {
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "VersionList");
            var diff = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "DiffScroll");
            Assert.True(list.Bounds.X > diff.Bounds.X, $"清單 X={list.Bounds.X}，差異 X={diff.Bounds.X}");
            Assert.True(list.Bounds.Y < diff.Bounds.Y + diff.Bounds.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task NarrowWindow_ListSitsAboveTheDiff()
    {
        var vm = await LoadThreeKindsAsync();
        var window = Host(vm, 400);
        try
        {
            // 寬度變化會在下一輪 UI 工作才重排
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "VersionList");
            var diff = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "DiffScroll");
            Assert.True(list.Bounds.Y < diff.Bounds.Y, $"清單 Y={list.Bounds.Y}，差異 Y={diff.Bounds.Y}");
            Assert.Equal(diff.Bounds.X, list.Bounds.X, 1);
        }
        finally
        {
            window.Close();
        }
    }
}
