using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lumi.App.Services;
using Lumi.App.ViewModels;
using System.ComponentModel;

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
        // 輸入框沒有自己的外框：取得焦點時，由外層 InputArea 用 Accent 邊框表達（純視覺，所以放在 code-behind）
        InputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsKeyboardFocusWithinProperty)
                InputArea.Classes.Set("focused", InputBox.IsKeyboardFocusWithin);
        };
        // 收音中按 Esc 取消錄音（焦點在輸入區內時；收音開始時焦點已經移到收音面板）
        AddHandler(KeyDownEvent, OnComposerKeyDown);
        DataContextChanged += (_, _) => AttachViewModel();
    }

    private WorkbenchViewModel? ViewModel => DataContext as WorkbenchViewModel;

    /// <summary>語音狀態上一次處理到哪裡：同一狀態的重複通知不重新搶焦點。</summary>
    private VoiceInputState _focusedVoiceState = VoiceInputState.Idle;

    private WorkbenchViewModel? _attached;

    public void FocusInput()
    {
        if (InputBox.IsEffectivelyVisible)
            InputBox.Focus();
    }

    private void AttachViewModel()
    {
        if (_attached is not null)
        {
            _attached.PropertyChanged -= OnViewModelPropertyChanged;
            _attached.FocusInputRequested -= OnFocusInputRequested;
        }
        _attached = ViewModel;
        if (_attached is not null)
        {
            _attached.PropertyChanged += OnViewModelPropertyChanged;
            _attached.FocusInputRequested += OnFocusInputRequested;
        }
    }

    private void OnFocusInputRequested(object? sender, EventArgs e) => FocusInput();

    /// <summary>語音狀態改變時搬移焦點：進入收音面板就停在它的預設按鈕；回到閒置就回輸入框（否則焦點會掉出輸入區）。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsVoiceFailed 在 RaiseVoiceProperties 裡排在最後，那時各列的可見性已經更新
        if (e.PropertyName != nameof(WorkbenchViewModel.IsVoiceFailed) || ViewModel is not { } viewModel)
            return;
        if (viewModel.VoiceState == _focusedVoiceState)
            return;
        _focusedVoiceState = viewModel.VoiceState;

        // 等版面更新（面板剛顯示出來時還不能接受焦點）再搬移
        if (viewModel.IsVoiceActive)
            Dispatcher.UIThread.Post(VoicePanelHost.FocusDefault, DispatcherPriority.Loaded);
        else
            Dispatcher.UIThread.Post(FocusInput, DispatcherPriority.Loaded);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || ViewModel is not { IsVoiceRecording: true } viewModel)
            return;
        e.Handled = true;
        viewModel.VoiceCancelCommand.Execute(null);
    }

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
