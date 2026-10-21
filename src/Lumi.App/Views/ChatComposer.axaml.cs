using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 輸入區。code-behind 只放視覺行為：Enter 送出（Shift+Enter 換行）、Ctrl／Cmd+V 貼上截圖、拖放檔案／資料夾。
/// <c>InputBox</c>、<c>InputArea</c> 等 x:Name 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 直接使用。
/// </summary>
public partial class ChatComposer : UserControl
{
    public ChatComposer()
    {
        InitializeComponent();

        // TextBox 自己會處理 Enter，要在 tunnel 階段先攔下來
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        // 拖放檔案／資料夾加入上下文；Ctrl／Cmd+V 貼上剪貼簿裡的截圖
        InputArea.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        InputArea.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        InputArea.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private WorkbenchViewModel? ViewModel => DataContext as WorkbenchViewModel;

    public void FocusInput() => InputBox.Focus();

    private async void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;

        if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            // 剪貼簿有圖片就加進上下文；沒有就照一般貼上文字
            e.Handled = true;
            if (!await viewModel.Context.PasteImageAsync())
                InputBox.Paste();
            return;
        }

        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;

        // 輸入法組字中的 Enter 不會走到這裡（由 IME 處理），所以不用另外判斷
        e.Handled = true;
        if (viewModel.SendCommand.CanExecute(null))
            viewModel.SendCommand.Execute(null);
    }

    // ---- 拖放 ----

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accept = ViewModel?.Context.CanChange == true && e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        InputArea.Classes.Set("dropping", accept);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => InputArea.Classes.Set("dropping", false);

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        InputArea.Classes.Set("dropping", false);
        e.Handled = true;
        if (ViewModel is not { } viewModel || e.DataTransfer.TryGetFiles() is not { } items)
            return;

        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await viewModel.Context.AddPathsAsync(paths);
    }
}
