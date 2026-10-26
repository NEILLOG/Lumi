using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 工作台的內容工具列：新對話、歷史對話、模型膠囊、「⋯」對話動作選單（重新命名、匯出、刪除）。沒有 code-behind 邏輯；
/// <c>CapsuleButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 用它在選了模型後關掉彈出選單。
/// </summary>
public partial class ChatToolbar : UserControl
{
    public ChatToolbar() => InitializeComponent();
}
