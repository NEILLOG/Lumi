using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

public partial class ImageAttachmentView : UserControl
{
    /// <summary>產生中佔位的計時器：每秒把經過的秒數寫回 ViewModel（不是產生中就停）。</summary>
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ImageAttachmentView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => OnTimerTick();
        AttachedToVisualTree += (_, _) => UpdateTimer();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        DataContextChanged += (_, _) => UpdateTimer();
    }

    /// <summary>點「放大檢視」或縮圖：用 Lumi 的看圖視窗開啟。</summary>
    private void ViewLarger_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImageAttachmentViewModel { ShowsImage: true, FilePath: { } path })
            return;
        var viewer = new ImageViewerWindow(path);
        if (TopLevel.GetTopLevel(this) is Window owner)
            viewer.Show(owner);
        else
            viewer.Show();
    }

    /// <summary>用其他程式開啟（系統預設程式）。</summary>
    private async void OpenExternal_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageAttachmentViewModel { ShowsImage: true } vm)
            await OpenWithSystemAsync(vm);
    }

    /// <summary>開啟圖片所在的資料夾（<c>LaunchDirectoryInfoAsync</c>：目前沒有「選取檔案」的能力，所以只開資料夾）。</summary>
    private async void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ImageAttachmentViewModel { FilePath: { } path } && System.IO.Path.GetDirectoryName(path) is { } folder
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchDirectoryInfoAsync(new System.IO.DirectoryInfo(folder));
    }

    private async Task OpenWithSystemAsync(ImageAttachmentViewModel vm)
    {
        if (vm.FilePath is { } path && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            await launcher.LaunchFileInfoAsync(new System.IO.FileInfo(path));
    }

    /// <summary>計時器只在掛在視窗樹上、而且是產生中的佔位時跑。</summary>
    private void UpdateTimer()
    {
        if (VisualRoot is not null && DataContext is ImageAttachmentViewModel { IsPending: true } vm)
        {
            vm.Tick(DateTimeOffset.UtcNow);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void OnTimerTick()
    {
        if (DataContext is ImageAttachmentViewModel { IsPending: true } vm)
            vm.Tick(DateTimeOffset.UtcNow);
        else
            _timer.Stop();
    }
}
