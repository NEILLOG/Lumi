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

    /// <summary>
    /// 點了「放大檢視」或縮圖時發生。有訂閱就交給訂閱者（看圖視窗，TODO(25G)）；沒有就用系統的預設程式開啟（R6：看圖視窗合進來前）。
    /// </summary>
    public event EventHandler<ImageAttachmentViewModel>? ViewLargerRequested;

    private async void ViewLarger_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImageAttachmentViewModel { ShowsImage: true } vm)
            return;
        var handler = ViewLargerRequested;
        if (handler is not null)
        {
            handler(this, vm);
            return;
        }
        // TODO(25G)：改開 Lumi 看圖視窗（看圖視窗合進來之前暫時用系統看圖程式）
        await OpenWithSystemAsync(vm);
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
