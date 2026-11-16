using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>研究本視窗的編輯模式（純文字編輯整份 content、貼圖）。TODO(35H)：Phase 0 只有空殼。</summary>
public sealed partial class ContextNoteEditorViewModel : ObservableObject
{
    private readonly IContextNoteService _notes;
    private readonly IClipboardImageService _clipboard;
    private readonly IConfirmService _confirm;

    public ContextNoteEditorViewModel(IContextNoteService notes, IClipboardImageService clipboard, IConfirmService confirm)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(confirm);
        _notes = notes;
        _clipboard = clipboard;
        _confirm = confirm;
    }

    /// <summary>開始編輯：呼叫 BeginUserEdit，載入內容。</summary>
    public void Begin(string noteId, string content) => throw new NotImplementedException();   // TODO(35H)

    /// <summary>存檔並結束編輯（EndUserEditAsync）。超過字數上限時回 false、不結束。</summary>
    public Task<bool> CommitAsync() => throw new NotImplementedException();   // TODO(35H)

    /// <summary>放棄修改（有改過先確認）。使用者取消確認時回 false。</summary>
    public Task<bool> DiscardAsync() => throw new NotImplementedException();   // TODO(35H)

    public bool IsDirty => false;   // TODO(35H)

    /// <summary>編輯結束（存檔或放棄），視窗切回預覽。</summary>
    public event EventHandler? Finished;

    // TODO(35H)：實作後拿掉。讓 Phase 0 的欄位與事件不觸發「未使用」警告
    internal void RaiseFinished() => Finished?.Invoke(this, EventArgs.Empty);
    internal (IContextNoteService, IClipboardImageService, IConfirmService) Services => (_notes, _clipboard, _confirm);
}
