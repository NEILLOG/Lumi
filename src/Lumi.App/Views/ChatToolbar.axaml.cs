using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 工作台的內容工具列：歷史、新對話、模型膠囊、設定。沒有 code-behind 邏輯；
/// <c>CapsuleButton</c> 由 Avalonia 產生同名欄位（internal），WorkbenchWindow 用它在選了模型後關掉彈出選單。
/// </summary>
public partial class ChatToolbar : UserControl
{
    public ChatToolbar() => InitializeComponent();
}
