using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>工作台頂端的「有新版」提醒；純顯示，行為都在 <see cref="ViewModels.UpdateNoticeViewModel"/>。</summary>
public partial class UpdateBanner : UserControl
{
    public UpdateBanner() => InitializeComponent();
}
