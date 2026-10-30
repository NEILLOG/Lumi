using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>工作台頂端的預算提醒列；純顯示，行為都在 <see cref="ViewModels.BudgetAlertViewModel"/>。</summary>
public partial class BudgetBanner : UserControl
{
    public BudgetBanner() => InitializeComponent();
}
