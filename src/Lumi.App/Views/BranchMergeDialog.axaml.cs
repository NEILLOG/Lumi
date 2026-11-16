using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 帶回對話框（M36 36H，仿 <see cref="ContextNoteStartDialog"/>）：開啟時開始整理第一步；
/// 完成（<see cref="BranchMergeDialogViewModel.Finished"/>）或被關閉（Esc、取消、關閉鈕）時結束，結果由 ViewModel 的 <c>Completion</c> 給。
/// </summary>
public partial class BranchMergeDialog : Window
{
    private readonly BranchMergeDialogViewModel _vm;
    private bool _closing;

    public BranchMergeDialog(BranchMergeDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm ?? throw new ArgumentNullException(nameof(vm));
        DataContext = vm;

        Opened += (_, _) => _ = _vm.StartAsync();
        Closing += (_, _) =>
        {
            _closing = true;
            _vm.Cancel();
        };
        _vm.Finished += (_, _) =>
        {
            if (!_closing)
                Close();
        };
    }
}
