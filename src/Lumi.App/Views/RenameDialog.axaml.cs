using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>重新命名對話的小視窗（仿 <see cref="ConfirmDialog"/>）：回傳輸入的文字，取消回 null。</summary>
public partial class RenameDialog : Window
{
    public RenameDialog(string currentTitle, int maxLength)
    {
        InitializeComponent();
        TitleBox.Text = currentTitle;
        TitleBox.MaxLength = maxLength;
        Opened += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
        SaveButton.Click += (_, _) => Close(TitleBox.Text);
        CancelButton.Click += (_, _) => Close(null);
    }
}
