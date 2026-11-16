using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>研究本的版本歷史（差異、退回）。TODO(35I)：Phase 0 只有空殼。</summary>
public sealed partial class ContextNoteHistoryViewModel : ObservableObject
{
    private readonly IContextNoteStore _store;
    private readonly IContextNoteService _notes;
    private readonly IConfirmService _confirm;

    public ContextNoteHistoryViewModel(IContextNoteStore store, IContextNoteService notes, IConfirmService confirm)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(confirm);
        _store = store;
        _notes = notes;
        _confirm = confirm;
    }

    public Task LoadAsync(string noteId) => Task.CompletedTask;   // TODO(35I)

    /// <summary>按了「回到研究本」，或退回完成。</summary>
    public event EventHandler? BackRequested;

    // TODO(35I)：實作後拿掉。讓 Phase 0 的欄位與事件不觸發「未使用」警告
    internal void RaiseBackRequested() => BackRequested?.Invoke(this, EventArgs.Empty);
    internal (IContextNoteStore, IContextNoteService, IConfirmService) Services => (_store, _notes, _confirm);
}
