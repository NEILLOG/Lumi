using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台頂端的通知列（ui-spec 第 13 節）：最多顯示最新 <see cref="MaxShown"/> 則，其餘以「還有 N 則」帶過；
/// 沒有任何通知時整列不佔空間（<see cref="IsVisible"/>）。清單的順序由 <see cref="NotificationCenter"/> 決定（新的在最上面）。
/// </summary>
public sealed partial class NotificationStripViewModel : ObservableObject
{
    public const int MaxShown = 3;

    private readonly ObservableCollection<NotificationItemViewModel> _items;

    public NotificationStripViewModel(ObservableCollection<NotificationItemViewModel> items, Action dismissAll)
    {
        _items = items;
        DismissAllCommand = new RelayCommand(dismissAll);
        _items.CollectionChanged += (_, _) => Refresh();
        Refresh();
    }

    /// <summary>目前要顯示的項目（最多 <see cref="MaxShown"/> 則）。</summary>
    public ObservableCollection<NotificationItemViewModel> Shown { get; } = new();

    public bool IsVisible => _items.Count > 0;

    /// <summary>超出顯示上限、被收起來的則數。</summary>
    public int HiddenCount => Math.Max(0, _items.Count - MaxShown);

    public bool HasHidden => HiddenCount > 0;

    public string HiddenText => string.Format(CultureInfo.CurrentCulture, Strings.Notifications_MoreCount, HiddenCount);

    public IRelayCommand DismissAllCommand { get; }

    private void Refresh()
    {
        Shown.Clear();
        foreach (var item in _items.Take(MaxShown))
            Shown.Add(item);

        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(HiddenCount));
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(HiddenText));
    }
}
