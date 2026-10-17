using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

public partial class ApiKeyDialog : Window
{
    public ApiKeyDialog(ApiKeyDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += saved => Close(saved);
        Opened += (_, _) => KeyBox.Focus();
    }
}
