using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lumi.App.ViewModels;

/// <summary>
/// 環境變數或 HTTP 標頭的一列。值只在輸入時存在記憶體裡；已儲存的值永遠不讀回畫面（只顯示「已設定」），
/// 要改就按「更換」重新輸入，移除則是把這一列拿掉（儲存時整份祕密一起更新）。
/// </summary>
public sealed partial class McpSecretRowViewModel : ObservableObject
{
    private readonly Func<McpSecretRowViewModel, string?> _nameError;
    private readonly Action<McpSecretRowViewModel> _remove;
    private readonly Action _changed;

    /// <param name="isSaved">已經存在於設定裡的一列（名稱固定，值只顯示「已設定」）；false＝這次新增的一列。</param>
    /// <param name="nameError">名稱的錯誤文字（由擁有者計算，重複檢查要看其他列）。</param>
    /// <param name="remove">按「移除」時呼叫，由擁有者把這一列拿掉。</param>
    /// <param name="changed">這一列改了名稱、值或更換狀態時呼叫，讓擁有者重新計算表單狀態。</param>
    public McpSecretRowViewModel(string name, bool isSaved, Func<McpSecretRowViewModel, string?> nameError,
        Action<McpSecretRowViewModel> remove, Action changed)
    {
        _name = name;
        IsSaved = isSaved;
        _nameError = nameError;
        _remove = remove;
        _changed = changed;
    }

    public bool IsSaved { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>值（遮罩顯示）。已儲存的列在更換前是空的。</summary>
    [ObservableProperty]
    private string _value = "";

    /// <summary>已儲存的列按了「更換」：顯示輸入框。</summary>
    [ObservableProperty]
    private bool _isReplacing;

    /// <summary>名稱的錯誤文字（格式錯或重複）；通過是 null。</summary>
    public string? NameError => _nameError(this);

    /// <summary>顯示輸入框：新增的列，或正在更換的列。</summary>
    public bool ShowInput => !IsSaved || IsReplacing;

    /// <summary>顯示「已設定」與更換、移除：已儲存、而且不在更換中。</summary>
    public bool ShowStored => IsSaved && !IsReplacing;

    /// <summary>這一列會改變祕密：新增的列，或更換了值的列（更換後留空就視為沒有改）。</summary>
    public bool IsChanged => !IsSaved || (IsReplacing && Value.Length > 0);

    [RelayCommand]
    private void Replace()
    {
        IsReplacing = true;
        Value = "";
    }

    [RelayCommand]
    private void CancelReplace()
    {
        IsReplacing = false;
        Value = "";
    }

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnNameChanged(string value) => _changed();

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsChanged));
        _changed();
    }

    partial void OnIsReplacingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInput));
        OnPropertyChanged(nameof(ShowStored));
        OnPropertyChanged(nameof(IsChanged));
        _changed();
    }

    /// <summary>其他列改名時，重新計算名稱的錯誤（重複檢查會看到其他列）。</summary>
    public void RefreshNameError() => OnPropertyChanged(nameof(NameError));
}
