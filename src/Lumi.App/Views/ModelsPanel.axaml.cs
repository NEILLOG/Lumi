using Avalonia.Controls;
using Avalonia.Threading;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 「AI 連線」頁的模型設定分段：預設模型、清單卡片＋詳細表單。DataContext 是 <see cref="ModelsViewModel"/>。
/// code-behind 只放視覺行為：「進階」（最大輸出、上下文上限）有錯誤時自動展開，不讓錯誤藏在收合的區段裡。
/// </summary>
public partial class ModelsPanel : UserControl
{
    public ModelsPanel()
    {
        InitializeComponent();
        InnerTextBoxName.Forward(ModelIdBox, "PART_EditableTextBox");
        InnerTextBoxName.Forward(MaxOutputBox, "PART_TextBox");
        InnerTextBoxName.Forward(ContextWindowBox, "PART_TextBox");
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ModelsViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(ModelsViewModel.MaxOutputError) or nameof(ModelsViewModel.ContextWindowError))
                        Dispatcher.UIThread.Post(() => ExpandAdvancedWhenNeeded(vm), DispatcherPriority.Loaded);   // 載入表單時欄位一個一個設定，等整個表單設好再看有沒有錯誤
                };
                ExpandAdvancedWhenNeeded(vm);
            }
        };
    }

    private void ExpandAdvancedWhenNeeded(ModelsViewModel vm)
    {
        if (vm.HasEditor && (vm.MaxOutputError is not null || vm.ContextWindowError is not null))
            AdvancedExpander.IsExpanded = true;
    }
}
