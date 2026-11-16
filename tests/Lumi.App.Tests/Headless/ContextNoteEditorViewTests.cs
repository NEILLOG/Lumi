using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Headless.XUnit;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.App.Views;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Tests.Headless;

/// <summary>M35 35H：研究本編輯模式實際渲染出來的樣子（輸入框的無障礙名稱、按鈕層級、超過字數時的停用）。</summary>
public class ContextNoteEditorViewTests
{
    private sealed class Rig : IDisposable
    {
        public ContextNoteEditorViewModel Vm { get; }
        public ContextNoteEditorView View { get; } = new();
        public Window Window { get; } = new();

        public Rig()
        {
            Vm = new ContextNoteEditorViewModel(new FakeContextNoteService(), new FakeClipboardImage(), new FakeConfirmService());
            Vm.Begin("note-1", "## 已決定\n\n- 甲");
            View.DataContext = Vm;
            Window.Content = View;
            Window.Show();
            Window.UpdateLayout();
        }

        public TextBox Editor => View.GetVisualDescendants().OfType<TextBox>().Single();

        public Button FindButton(string text) => View.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, text));

        public void Dispose() => Window.Close();
    }

    [AvaloniaFact]
    public void Editor_IsMultilineMonospaced_WithAccessibleName()
    {
        using var rig = new Rig();

        var editor = rig.Editor;
        Assert.True(editor.AcceptsReturn);
        Assert.True(editor.AcceptsTab);
        Assert.Equal(TextWrapping.Wrap, editor.TextWrapping);
        Assert.Equal(Strings.ContextNote_Edit_AutomationName, AutomationProperties.GetName(editor));
        Assert.Equal("## 已決定\n\n- 甲", editor.Text);
    }

    [AvaloniaFact]
    public void Buttons_DiscardIsTertiary_DoneIsPrimary()
    {
        using var rig = new Rig();

        var discard = rig.FindButton(Strings.ContextNote_Edit_Discard);
        var done = rig.FindButton(Strings.ContextNote_Edit_Done);
        Assert.Contains("tertiary", discard.Classes);
        Assert.DoesNotContain("primary", discard.Classes);
        Assert.Contains("primary", done.Classes);
        Assert.DoesNotContain("tertiary", done.Classes);
        Assert.True(done.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Done_IsDisabled_WhenOverLimit()
    {
        using var rig = new Rig();

        rig.Editor.Text = new string('字', ContextNoteLimits.NoteMaxChars + 1);

        Assert.True(rig.Vm.IsTooLong);
        Assert.False(rig.FindButton(Strings.ContextNote_Edit_Done).IsEffectivelyEnabled);
        Assert.True(rig.FindButton(Strings.ContextNote_Edit_Discard).IsEffectivelyEnabled);
    }
}
