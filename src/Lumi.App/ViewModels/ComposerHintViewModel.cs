using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumi.App.ViewModels;

/// <summary>
/// 輸入框上方的使用習慣提示列（plan/23-usage-cost.md 第 7.1 節）。這裡只負責顯示與按鈕；
/// 提示的內容與各按鈕要做的事（含寫入紀錄）由 <c>UsageHintCoordinator</c> 與 <c>WorkbenchViewModel</c> 組好，用 <see cref="Show"/> 交進來。
/// </summary>
public sealed partial class ComposerHintViewModel : ObservableObject
{
    private Func<Task>? _primary;
    private Func<Task>? _secondary;
    private Func<Task>? _dismiss;
    private Func<Task>? _mute;

    /// <summary>提示的文字；null 表示沒有提示（列隱藏）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private string? _text;

    public bool IsVisible => Text is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimary))]
    private string? _primaryText;

    public bool HasPrimary => PrimaryText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondary))]
    private string? _secondaryText;

    public bool HasSecondary => SecondaryText is not null;

    /// <summary>顯示一則提示（取代目前的內容；呼叫端要先處理舊的，見 <see cref="CloseAsync"/>）。</summary>
    public void Show(ComposerHintContent content)
    {
        _primary = content.Primary;
        _secondary = content.Secondary;
        _dismiss = content.Dismiss;
        _mute = content.Mute;
        PrimaryText = content.PrimaryText;
        SecondaryText = content.SecondaryText;
        Text = content.Text;
    }

    /// <summary>收起目前的提示並記為「知道了」（Dismissed）。沒有提示時什麼都不做。送出、切換對話、被新的提示取代時使用。</summary>
    public async Task CloseAsync()
    {
        if (!IsVisible)
            return;
        var dismiss = _dismiss;
        Hide();
        if (dismiss is not null)
            await dismiss();
    }

    /// <summary>收起提示，不寫紀錄。</summary>
    public void Hide()
    {
        _primary = null;
        _secondary = null;
        _dismiss = null;
        _mute = null;
        PrimaryText = null;
        SecondaryText = null;
        Text = null;
    }

    /// <summary>「知道了」。</summary>
    [RelayCommand]
    private Task DismissAsync() => CloseAsync();

    /// <summary>「不再提醒這類」：收起並寫入靜音設定。</summary>
    [RelayCommand]
    private async Task MuteAsync()
    {
        var mute = _mute;
        Hide();
        if (mute is not null)
            await mute();
    }

    /// <summary>第一個動作按鈕（記為已處理，再執行動作）。</summary>
    [RelayCommand]
    private async Task PrimaryAsync()
    {
        var action = _primary;
        Hide();
        if (action is not null)
            await action();
    }

    /// <summary>第二個動作按鈕（記為已處理，再執行動作）。</summary>
    [RelayCommand]
    private async Task SecondaryAsync()
    {
        var action = _secondary;
        Hide();
        if (action is not null)
            await action();
    }
}

/// <summary>一則提示的內容與按鈕行為。<see cref="Dismiss"/> 與 <see cref="Mute"/> 負責寫紀錄（與設定）；<see cref="Primary"/>／<see cref="Secondary"/> 負責紀錄為已處理後執行動作。</summary>
public sealed record ComposerHintContent(
    string Text,
    string? PrimaryText,
    Func<Task>? Primary,
    string? SecondaryText,
    Func<Task>? Secondary,
    Func<Task> Dismiss,
    Func<Task> Mute);
