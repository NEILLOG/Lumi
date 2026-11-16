using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>研究本視窗的編輯模式（純文字編輯整份 content、貼圖）。</summary>
public sealed partial class ContextNoteEditorViewModel : ObservableObject
{
    private readonly IContextNoteService _notes;
    private readonly IClipboardImageService _clipboard;
    private readonly IConfirmService _confirm;

    private string? _noteId;
    private string _original = string.Empty;

    public ContextNoteEditorViewModel(IContextNoteService notes, IClipboardImageService clipboard, IConfirmService confirm)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(clipboard);
        ArgumentNullException.ThrowIfNull(confirm);
        _notes = notes;
        _clipboard = clipboard;
        _confirm = confirm;

        CommitCommand = new AsyncRelayCommand(() => CommitAsync(), () => !IsTooLong);
        DiscardCommand = new AsyncRelayCommand(() => DiscardAsync());
    }

    /// <summary>整份研究本的文字（雙向綁定到 TextBox）。</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>上一次操作的錯誤訊息；沒有錯誤是 null。</summary>
    [ObservableProperty]
    private string? _errorText;

    public IAsyncRelayCommand CommitCommand { get; }

    public IAsyncRelayCommand DiscardCommand { get; }

    /// <summary>字數說明（例如「1234 / 20000」），顯示在下方說明列的右側。</summary>
    public string CharCountText => string.Format(CultureInfo.InvariantCulture, "{0} / {1}", Text.Length, ContextNoteLimits.NoteMaxChars);

    /// <summary>超過字數上限：「完成」停用，改顯示 <see cref="TooLongText"/>。</summary>
    public bool IsTooLong => Text.Length > ContextNoteLimits.NoteMaxChars;

    public string TooLongText => string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Edit_TooLongFormat,
        ContextNoteLimits.NoteMaxChars, Text.Length);

    /// <summary>目前的文字與開始編輯時不同。</summary>
    public bool IsDirty => !string.Equals(Text, _original, StringComparison.Ordinal);

    /// <summary>編輯結束（存檔或放棄），視窗切回預覽。</summary>
    public event EventHandler? Finished;

    /// <summary>開始編輯：呼叫 BeginUserEdit，載入內容。</summary>
    public void Begin(string noteId, string content)
    {
        _noteId = noteId;
        _original = content;
        Text = content;
        ErrorText = null;
        _notes.BeginUserEdit(noteId);
    }

    /// <summary>存檔並結束編輯（EndUserEditAsync）。超過字數上限時回 false、不結束。</summary>
    public async Task<bool> CommitAsync()
    {
        if (_noteId is not { } noteId || IsTooLong)
            return false;

        ErrorText = null;
        try
        {
            await _notes.EndUserEditAsync(noteId, IsDirty ? Text : null);
        }
        catch (ArgumentException)
        {
            // Core 拒絕這份內容。沒有通用的「儲存失敗」字串，先沿用字數說明（見報告）
            ErrorText = TooLongText;
            return false;
        }

        Finished?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>放棄修改（有改過先確認）。使用者取消確認時回 false。</summary>
    public async Task<bool> DiscardAsync()
    {
        if (_noteId is not { } noteId)
            return false;

        if (IsDirty && !await _confirm.ConfirmDestructiveAsync(Strings.ContextNote_Edit_DiscardConfirm, Strings.ContextNote_Edit_Discard))
            return false;

        await _notes.EndUserEditAsync(noteId, null);
        Finished?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 貼上剪貼簿的圖片：存進 images/，在游標所在行的下一行插入 <c>![圖片](相對路徑)</c>（前後各一個換行）。
    /// 回傳插入的那一行之後的游標位置；沒有圖片回 -1（不做事）；存不進來也回 -1，<see cref="ErrorText"/> 有說明。
    /// </summary>
    public async Task<int> PasteImageAsync(int caretIndex)
    {
        ErrorText = null;
        if (_noteId is not { } noteId || await _clipboard.TryGetImageAsync() is not { } image)
            return -1;

        string path;
        try
        {
            path = await _notes.AddImageAsync(noteId, image.Data, image.MediaType);
        }
        catch (ArgumentException)
        {
            ErrorText = Strings.ContextNote_Edit_PasteImageFailed;
            return -1;
        }

        return InsertImageLine(Text, Math.Clamp(caretIndex, 0, Text.Length), $"![圖片]({path})");
    }

    /// <summary>
    /// 在 <paramref name="caret"/> 所在行的下一行插入獨立的一行 <paramref name="line"/>，前後都有換行。
    /// 回傳插入的那一行之後（下一行開頭，或文字結尾）的位置。
    /// </summary>
    private int InsertImageLine(string text, int caret, string line)
    {
        var newline = text.IndexOf('\n', caret);
        // 游標所在行有結尾換行 → 插在它後面；最後一行沒有結尾換行 → 插在文字結尾，前面先補一個換行
        var at = newline >= 0 ? newline + 1 : text.Length;
        var prefix = at > 0 && text[at - 1] != '\n' ? "\n" : string.Empty;
        var inserted = prefix + line + "\n";

        Text = text.Insert(at, inserted);
        return at + inserted.Length;
    }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CharCountText));
        OnPropertyChanged(nameof(IsTooLong));
        OnPropertyChanged(nameof(TooLongText));
        OnPropertyChanged(nameof(IsDirty));
        CommitCommand.NotifyCanExecuteChanged();
    }
}
