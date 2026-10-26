using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 工作台的內容工具列：新對話、歷史對話、模型膠囊、用量圓環（點開面板）、「⋯」對話動作選單。
/// code-behind 只接線用量面板的開關狀態與關閉後的焦點：面板關閉時焦點回到用量按鈕（Esc 與按鈕動作都一樣）。
/// <c>CapsuleButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 用它在選了模型後關掉彈出選單。
/// </summary>
public partial class ChatToolbar : UserControl
{
    public ChatToolbar()
    {
        InitializeComponent();

        if (UsageButton.Flyout is Flyout flyout)
        {
            // 面板開啟中：按鈕底色與框改成選取樣式（class open）
            flyout.Opened += (_, _) => UsageButton.Classes.Add("open");
            flyout.Closed += (_, _) => UsageButton.Classes.Remove("open");
            if (flyout.Content is UsageFlyout panel)
            {
                panel.Dismissed += (_, _) =>
                {
                    flyout.Hide();
                    UsageButton.Focus();
                };
            }
        }
    }
}
