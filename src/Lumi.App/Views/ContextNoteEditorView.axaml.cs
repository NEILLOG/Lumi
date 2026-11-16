using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 研究本的編輯模式（DataContext 是 <see cref="ContextNoteEditorViewModel"/>）。
/// code-behind 只做一件事：攔 Ctrl／Cmd+V，剪貼簿有圖片時交給 ViewModel 插入圖片連結，否則照常貼文字。
/// </summary>
public partial class ContextNoteEditorView : UserControl
{
    public ContextNoteEditorView()
    {
        InitializeComponent();
        EditorBox.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    private async void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ContextNoteEditorViewModel viewModel)
            return;
        if (e.Key != Key.V || !(e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
            return;

        e.Handled = true;
        var caret = await viewModel.PasteImageAsync(EditorBox.CaretIndex);
        if (caret >= 0)
        {
            EditorBox.CaretIndex = caret;
            return;
        }

        // 剪貼簿沒有圖片（而且沒有錯誤）才貼文字；圖片存不進來時錯誤已經顯示，不貼文字
        if (viewModel.ErrorText is null)
            EditorBox.Paste();
    }
}
