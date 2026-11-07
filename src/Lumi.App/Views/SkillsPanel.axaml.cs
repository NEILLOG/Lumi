using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定視窗「技能」的內容（M29 包 29C）。頁首與「新增技能」在 <see cref="SettingsWindow"/>；這裡的行為全部在 <c>SkillsViewModel</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class SkillsPanel : UserControl
{
    public SkillsPanel()
    {
        InitializeComponent();
    }
}
