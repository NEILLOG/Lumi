using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Usage;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台頂端的預算提醒列（plan/23-usage-cost.md 第 5 節）：跨過 80% 是 Warning，跨過 100% 是 Danger。
/// 只提醒、不停止回應。由 <c>BudgetAlertCoordinator</c> 呼叫 <see cref="Show"/>；沒有提醒時整列不佔空間。
/// </summary>
public sealed partial class BudgetAlertViewModel : ObservableObject
{
    private readonly Action? _openUsagePage;

    /// <param name="openUsagePage">「查看用量」要做的事（開設定視窗到用量與費用）；沒有時按了只關掉提醒列。</param>
    public BudgetAlertViewModel(Action? openUsagePage = null)
    {
        _openUsagePage = openUsagePage;
    }

    /// <summary>目前的提醒門檻；null 表示沒有提醒（整列隱藏）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(IsDanger))]
    private BudgetAlertLevel? _level;

    public bool IsVisible => Level is not null;

    /// <summary>100%：Danger 樣式；80%：Warning 樣式。</summary>
    public bool IsDanger => Level == BudgetAlertLevel.Hundred;

    /// <summary>「這個月的 AI 費用已經用到預算的 80%（NT$488 ／ NT$800）。」</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>顯示提醒。<paramref name="spentUsd"/> 是本月已花費、<paramref name="budgetUsd"/> 是每月預算（美元）。</summary>
    public void Show(BudgetAlertLevel level, decimal spentUsd, decimal budgetUsd)
    {
        var format = level == BudgetAlertLevel.Hundred ? Strings.Budget_Alert100Format : Strings.Budget_Alert80Format;
        Text = string.Format(CultureInfo.CurrentCulture, format, MoneyFormat.Twd(spentUsd), MoneyFormat.Twd(budgetUsd));
        Level = level;
    }

    /// <summary>「查看用量」：開設定視窗到用量與費用，提醒列同時關掉。</summary>
    [RelayCommand]
    private void ViewUsage()
    {
        Level = null;
        _openUsagePage?.Invoke();
    }

    /// <summary>「知道了」：關掉提醒列（同一個門檻本月不會再提醒）。</summary>
    [RelayCommand]
    private void Dismiss() => Level = null;
}
