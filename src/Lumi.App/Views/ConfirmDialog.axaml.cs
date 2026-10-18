using Avalonia.Controls;

namespace Lumi.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string message, string confirmText)
    {
        InitializeComponent();
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }
}
