using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

public partial class WorkbenchWindow : Window
{
    public WorkbenchWindow(WorkbenchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
