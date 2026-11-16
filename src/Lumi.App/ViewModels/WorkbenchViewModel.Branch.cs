using CommunityToolkit.Mvvm.Input;

namespace Lumi.App.ViewModels;

/// <summary>工作台的支線（M36）：開支線、問這段、打開支線、狀態。TODO(36F)：Phase 0 只有宣告。</summary>
public partial class WorkbenchViewModel
{
    /// <summary>從一則回答開支線（動作列）。</summary>
    [RelayCommand]
    private Task StartBranchAsync(MessageViewModel? message) => Task.CompletedTask;   // TODO(36F)

    /// <summary>「問這段」。</summary>
    [RelayCommand]
    private Task AskAboutSelectionAsync(BranchQuoteRequest? request) => Task.CompletedTask;   // TODO(36F)

    /// <summary>打開一條支線（摘要卡的「看完整支線」、歷史抽屜）。</summary>
    [RelayCommand]
    private Task OpenBranchAsync(string? conversationId) => Task.CompletedTask;   // TODO(36F)

    /// <summary>目前開著的是支線（進行中或已結束）。訊息區的色條綁這個。</summary>
    public bool IsInBranch => false;   // TODO(36F)：改成依 _branchState 計算並在 LoadAsync 後通知

    /// <summary>新建支線之後發，View 播進入動畫。</summary>
    public event EventHandler? BranchEntered;

    // TODO(36F)：實作後拿掉。讓 Phase 0 的事件不觸發「未使用」警告
    internal void RaiseBranchEntered() => BranchEntered?.Invoke(this, EventArgs.Empty);
}
