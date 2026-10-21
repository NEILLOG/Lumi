using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Lumi.App.Views;

/// <summary>
/// 可編輯的 ComboBox 與 NumericUpDown 在範本裡有一個真正拿到焦點的 TextBox：螢幕閱讀器念的是它，
/// 所以把外層的 <c>AutomationProperties.Name</c> 轉給它（只設在外層，焦點進到輸入框時會念不出名稱）。
/// </summary>
internal static class InnerTextBoxName
{
    public static void Forward(TemplatedControl owner, string partName)
    {
        owner.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<TextBox>(partName) is { } box)
                AutomationProperties.SetName(box, AutomationProperties.GetName(owner));
        };
    }
}
