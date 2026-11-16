using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests.Headless;

/// <summary>研究本視窗（M35 35G）的外觀與版面：三種模式、窄寬度、深淺主題、並排按鈕的無障礙名稱。</summary>
public class ContextNoteWindowTests
{
    private const string Conversation = "conv-w";

    private static ContextNoteViewModel MakeViewModel(out FakeContextNoteService notes)
    {
        notes = new FakeContextNoteService();
        var note = new ContextNote("note-w", Conversation, "評估本機模型", true, 1, 0, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        const string content = "## 問題拆解\n- 第一點\n\n## 已決定\n- 決定甲\n\n## 待決問題\n\n## 觀點與選項\n\n## 否決的選項\n\n## 目前的解與待驗證\n\n## 旁支\n";
        var current = new ContextNoteRevision(ContextNoteRevision.NewId(), note.Id, 1, ContextNoteRevisionKind.ScribeFirst,
            note.Goal, content, [], null, 0, DateTimeOffset.UtcNow);
        notes.Views[Conversation] = new ContextNoteView(note, current, null);

        var files = new InMemoryContextNoteFiles();
        var editor = new ContextNoteEditorViewModel(notes, new FakeClipboardImage(), new FakeConfirmService());
        var history = new ContextNoteHistoryViewModel(new InMemoryContextNoteStore(), notes, new FakeConfirmService());
        return new ContextNoteViewModel(notes, files, new InMemoryConversationStore(), new SyncUiDispatcher(),
            new FakeFolderOpener(), new FakeFilePicker(), new FakeTimeProvider(), editor, history);
    }

    private static async Task<ContextNoteWindow> ShowAsync(ContextNoteViewModel vm, double? width = null, ThemeVariant? theme = null)
    {
        var window = new ContextNoteWindow(vm);
        if (width is { } w)
            window.Width = w;
        if (theme is { } variant)
            window.RequestedThemeVariant = variant;
        await vm.LoadAsync(Conversation);
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static bool Visible(Control control) => control.IsEffectivelyVisible;

    [AvaloniaFact]
    public async Task Window_Opens_WithTheTitleBarAndDefaults()
    {
        var vm = MakeViewModel(out _);
        var window = await ShowAsync(vm);

        Assert.True(window.IsVisible);
        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(720, window.Width);
        Assert.Equal(760, window.Height);
        Assert.Equal(480, window.MinWidth);
        Assert.Equal(420, window.MinHeight);
        var bar = window.FindControl<CustomTitleBar>("TitleBar");
        Assert.NotNull(bar);
        Assert.False(bar!.ShowSpriteButton);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Mode_ShowsOnlyTheMatchingView()
    {
        var vm = MakeViewModel(out _);
        var window = await ShowAsync(vm);
        var preview = window.GetVisualDescendants().OfType<ContextNotePreviewView>().Single();
        var editor = window.GetVisualDescendants().OfType<ContextNoteEditorView>().Single();
        var history = window.GetVisualDescendants().OfType<ContextNoteHistoryView>().Single();

        Assert.True(Visible(preview));
        Assert.False(Visible(editor));
        Assert.False(Visible(history));

        vm.Mode = ContextNoteWindowMode.Edit;
        window.UpdateLayout();
        Assert.False(Visible(preview));
        Assert.True(Visible(editor));
        Assert.False(Visible(history));

        vm.Mode = ContextNoteWindowMode.History;
        window.UpdateLayout();
        Assert.False(Visible(preview));
        Assert.False(Visible(editor));
        Assert.True(Visible(history));

        window.Close();
    }

    [AvaloniaFact]
    public async Task NarrowWidth_KeepsTheToolbarButtonsInsideTheWindow()
    {
        var vm = MakeViewModel(out _);
        vm.StatusText = "第 1 版 · 09:05 更新（這一段狀態文字很長很長很長很長很長很長很長很長很長很長很長很長）";
        var window = await ShowAsync(vm, width: 480);
        window.UpdateLayout();

        foreach (var name in new[] { "HistoryButton", "SideBySideButton", "MoreButton", "AutoOrganizeSwitch" })
        {
            var control = window.FindControl<Control>(name);
            Assert.NotNull(control);
            var origin = control!.TranslatePoint(default, window);
            Assert.NotNull(origin);
            Assert.True(origin!.Value.X >= 0, $"{name} 超出左邊");
            Assert.True(origin.Value.X + control.Bounds.Width <= window.ClientSize.Width, $"{name} 超出右邊");
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task LightAndDarkThemes_RenderWithoutErrors()
    {
        var vm = MakeViewModel(out _);
        var light = await ShowAsync(vm, theme: ThemeVariant.Light);
        Assert.True(light.IsVisible);
        light.Close();

        var dark = await ShowAsync(vm, theme: ThemeVariant.Dark);
        Assert.True(dark.IsVisible);
        dark.Close();
    }

    [AvaloniaFact]
    public async Task SideBySideButton_HasAnAccessibleName()
    {
        var vm = MakeViewModel(out _);
        var window = await ShowAsync(vm);

        var button = window.FindControl<Button>("SideBySideButton");
        Assert.NotNull(button);
        Assert.Equal(Strings.ContextNote_SideBySide, AutomationProperties.GetName(button!));
        window.Close();
    }
}
