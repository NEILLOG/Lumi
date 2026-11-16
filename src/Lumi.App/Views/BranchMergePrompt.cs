using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;
using Lumi.Core.Branches;

namespace Lumi.App.Views;

/// <summary>
/// 以作用中的視窗為 owner 開啟帶回對話框（和 <see cref="ContextNoteStartPrompt"/> 同一個 owner 規則）。
/// 沒有 owner 時不能開對話框，什麼都不帶回（<c>OpenTo</c> 是 null）。
/// </summary>
public sealed class BranchMergePrompt : IBranchMergePrompt
{
    private readonly IBranchService _branches;

    public BranchMergePrompt(IBranchService branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        _branches = branches;
    }

    public async Task<BranchMergeResult> PromptAsync(BranchMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
        if (owner is null)
            return new BranchMergeResult(null);

        var vm = new BranchMergeDialogViewModel(_branches, request);
        var dialog = new BranchMergeDialog(vm);
        await dialog.ShowDialog(owner);
        return await vm.Completion;
    }
}
