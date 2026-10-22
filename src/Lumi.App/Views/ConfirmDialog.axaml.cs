using Avalonia.Controls;

namespace Lumi.App.Views;

public partial class ConfirmDialog : Window
{
    /// <param name="destructive">破壞性動作（刪除、清除、放棄變更）：確認鍵是 danger 風格；否則是 primary。</param>
    public ConfirmDialog(string message, string confirmText, bool destructive = false)
    {
        InitializeComponent();
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        // XAML 預設是 primary；破壞性動作換成 danger（兩者互斥，確認區永遠只有一個 primary 或 danger）
        if (destructive)
        {
            ConfirmButton.Classes.Remove("primary");
            ConfirmButton.Classes.Add("danger");
        }
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
