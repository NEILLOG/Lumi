using Avalonia.Controls;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

public partial class ModelPicker : UserControl
{
    public ModelPicker()
    {
        InitializeComponent();
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ModelSwitcherViewModel vm || EntryList.SelectedItem is not ModelPickerEntry entry)
            return;

        // 選單每次打開都是重新選：選完把選取清掉，下次才能再選同一個
        EntryList.SelectedItem = null;
        await vm.SelectAsync(entry);
    }
}
