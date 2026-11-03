using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lumi.App.Views;

/// <summary>
/// 工作台的內容工具列：新對話、歷史對話、模型膠囊、用量圓環（點開面板）、「⋯」對話動作選單。
/// code-behind 只接線用量面板的開關狀態與關閉後的焦點：面板關閉時焦點回到用量按鈕（Esc 與按鈕動作都一樣）。
/// <c>CapsuleButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 用它在選了模型後關掉彈出選單。
/// </summary>
public partial class ChatToolbar : UserControl
{
    /// <summary>
    /// 和 XAML 的 <c>Grid ColumnSpacing="12"</c> 相同：模型選單欄與左右兩組按鈕之間的間距。
    /// 改 XAML 的間距時這裡要一起改（<see cref="CapsuleColumnIn"/> 靠它算欄位邊界）。
    /// </summary>
    private const double ColumnSpacing = 12;

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

    /// <summary>
    /// 小精靈角色讓位：模型選單那一欄的右邊留出 <paramref name="width"/> 的空白給角色（用 Margin 實現，長模型名稱因此省略）。
    /// 0 時與沒有保留完全相同。
    /// </summary>
    public void SetCapsuleReserve(double width)
    {
        CapsuleButton.Margin = new Thickness(0, 0, Math.Max(0, width), 0);
    }

    /// <summary>
    /// 模型選單那一欄（第 1 欄）在 <paramref name="relativeTo"/> 座標裡的左右邊界：
    /// 左 ＝ 左側按鈕群右緣 ＋ <see cref="ColumnSpacing"/>，右 ＝ 右側按鈕群左緣 － <see cref="ColumnSpacing"/>。
    /// 座標翻譯不出來時回傳 null。
    /// </summary>
    public (double Left, double Right)? CapsuleColumnIn(Visual relativeTo)
    {
        var left = ToolbarLeftButtons.TranslatePoint(new Point(ToolbarLeftButtons.Bounds.Width, 0), relativeTo);
        var right = ToolbarRightButtons.TranslatePoint(new Point(0, 0), relativeTo);
        if (left is not { } leftPoint || right is not { } rightPoint)
            return null;
        return (leftPoint.X + ColumnSpacing, rightPoint.X - ColumnSpacing);
    }
}
