using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>
/// 建議卡片（M35 4.9）：`suggest_context_note` 的成功結果，是一種回覆附件，自成一列、放在那則回覆的文字上方。
/// 狀態不存在卡片裡，從宿主（工作台）讀；宿主的狀態變了就更新。
/// </summary>
public sealed partial class NoteSuggestionAttachmentViewModel : ReplyAttachmentViewModel
{
    private readonly INoteSuggestionHost _host;

    public NoteSuggestionAttachmentViewModel(string callId, string goal, INoteSuggestionHost host) : base(callId)
    {
        ArgumentNullException.ThrowIfNull(host);
        Goal = goal;
        _host = host;
        _state = host.SuggestionState;

        // 只用弱參考接收宿主的狀態變化：卡片被換掉（重新分配附件）後，宿主不會一直參照舊的卡片
        var weak = new WeakReference<NoteSuggestionAttachmentViewModel>(this);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (weak.TryGetTarget(out var self))
                self.Refresh();
            else
                host.SuggestionStateChanged -= handler;
        };
        host.SuggestionStateChanged += handler;
    }

    /// <summary>建議的目標（工具參數的 goal）。</summary>
    public string Goal { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsCreated), nameof(IsDismissed))]
    private NoteSuggestionState _state;

    public bool IsPending => State == NoteSuggestionState.Pending;

    public bool IsCreated => State == NoteSuggestionState.Created;

    public bool IsDismissed => State == NoteSuggestionState.Dismissed;

    /// <summary>回應中不能建立（C8）。</summary>
    public bool CanCreate => _host.CanCreate;

    private void Refresh()
    {
        State = _host.SuggestionState;
        CreateCommand.NotifyCanExecuteChanged();
        DismissCommand.NotifyCanExecuteChanged();
    }

    /// <summary>「開研究本」：帶著建議的目標進啟動對話框；取消時卡片仍然是等待中。</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => _host.CreateFromSuggestionAsync(Goal);

    /// <summary>「不用」：略過，這則對話不會再詢問。</summary>
    [RelayCommand(CanExecute = nameof(IsPending))]
    private Task DismissAsync() => _host.DismissSuggestionAsync();

    /// <summary>「打開研究本」。</summary>
    [RelayCommand]
    private Task OpenAsync() => _host.OpenNoteAsync();
}
