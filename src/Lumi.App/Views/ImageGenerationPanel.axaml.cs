using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 「AI 連線」頁詳細區的圖片生成設定表單（M22 22G）。DataContext 是 <see cref="ImageGenerationViewModel"/>。
/// code-behind 只放視覺行為：模型 ID 的內層文字框要有可存取名稱（與模型表單相同的做法）。
/// </summary>
public partial class ImageGenerationPanel : UserControl
{
    public ImageGenerationPanel()
    {
        InitializeComponent();
        InnerTextBoxName.Forward(ImageModelIdBox, "PART_EditableTextBox");
    }
}
