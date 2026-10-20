using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 一段連續的工具呼叫（中間沒有文字氣泡）。3 個以上合併成一張群組卡（ui-spec 第 4 節）；
/// 少於 3 個就直接各自顯示卡片。
/// </summary>
public partial class ToolRunViewModel : MessageViewModel
{
    /// <summary>連續幾個工具呼叫以上才合併成群組卡。</summary>
    public const int GroupThreshold = 3;

    public ToolRunViewModel() : base(isUser: false)
    {
        Cards.CollectionChanged += OnCardsChanged;
    }

    public override bool IsAssistant => false;

    public ObservableCollection<ToolCardViewModel> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupIsOpen), nameof(CanToggleGroup))]
    private bool _groupExpanded;

    /// <summary>內容卡（<c>show_details</c>）不算在連續呼叫的數量裡：它是給使用者看的內容，不是過程。</summary>
    public bool IsGroup => Cards.Count(c => !c.IsDetails) >= GroupThreshold;

    public bool ShowSingleCards => !IsGroup;

    private bool HasActive => Cards.Any(c => c.IsForcedOpen);

    private bool HasAwaiting => Cards.Any(c => c.IsAwaiting);

    private int FailedCount => Cards.Count(c => c.IsFailed);

    private int DeniedCount => Cards.Count(c => c.IsDenied);

    /// <summary>有卡片在等待允許或執行中時強制展開：否則確認按鈕會藏在收合的群組裡。</summary>
    public bool GroupIsOpen => HasActive || GroupExpanded;

    public bool CanToggleGroup => !HasActive;

    public bool GroupHasFailure => !HasActive && FailedCount > 0;

    public bool GroupHasAwaiting => HasAwaiting;

    public bool GroupAllSucceeded => !HasActive && FailedCount == 0 && DeniedCount == 0;

    public string GroupTitle
    {
        get
        {
            var breakdown = string.Join("、", Cards
                .GroupBy(c => c.DisplayName)
                .Select(g => string.Format(CultureInfo.CurrentCulture, Strings.Tool_GroupCount, g.Key, g.Count())));
            return string.Format(CultureInfo.CurrentCulture, Strings.Tool_GroupTitle, Cards.Count, breakdown, GroupStatus());
        }
    }

    private string GroupStatus()
    {
        if (HasAwaiting)
            return Strings.Tool_GroupAwaiting;
        if (HasActive)
            return Strings.Tool_GroupRunning;

        var parts = new List<string>();
        if (FailedCount > 0)
        {
            var names = string.Join("、", Cards.Where(c => c.IsFailed).Select(c => c.HasTarget ? $"{c.DisplayName} {c.Target}" : c.DisplayName));
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Tool_GroupFailed, FailedCount, names));
        }
        if (DeniedCount > 0)
            parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.Tool_GroupDenied, DeniedCount));
        return parts.Count == 0 ? Strings.Tool_GroupAllOk : string.Join("，", parts);
    }

    [RelayCommand(CanExecute = nameof(CanToggleGroup))]
    private void ToggleGroup() => GroupExpanded = !GroupExpanded;

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ToolCardViewModel card in e.NewItems)
                card.PropertyChanged += OnCardPropertyChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (ToolCardViewModel card in e.OldItems)
                card.PropertyChanged -= OnCardPropertyChanged;
        }
        RaiseGroupChanged();
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCardViewModel.State) or nameof(ToolCardViewModel.Target))
            RaiseGroupChanged();
    }

    private void RaiseGroupChanged()
    {
        OnPropertyChanged(nameof(IsGroup));
        OnPropertyChanged(nameof(ShowSingleCards));
        OnPropertyChanged(nameof(GroupIsOpen));
        OnPropertyChanged(nameof(CanToggleGroup));
        OnPropertyChanged(nameof(GroupHasFailure));
        OnPropertyChanged(nameof(GroupHasAwaiting));
        OnPropertyChanged(nameof(GroupAllSucceeded));
        OnPropertyChanged(nameof(GroupTitle));
        ToggleGroupCommand.NotifyCanExecuteChanged();
    }
}
