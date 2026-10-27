using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

public partial class ToolCardView : UserControl
{
    public ToolCardView()
    {
        InitializeComponent();
    }

    /// <summary>用系統的預設看圖程式開啟圖片（22F）。</summary>
    private async void OpenImage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToolCardImage { Exists: true } image } && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchFileInfoAsync(new System.IO.FileInfo(image.FilePath));
    }

    /// <summary>開啟圖片所在的資料夾（22F）。</summary>
    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ToolCardImage image } && System.IO.Path.GetDirectoryName(image.FilePath) is { } folder
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchDirectoryInfoAsync(new System.IO.DirectoryInfo(folder));
    }
}
