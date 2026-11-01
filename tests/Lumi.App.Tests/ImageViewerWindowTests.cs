using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Lumi.App.Views;
using SkiaSharp;

namespace Lumi.App.Tests;

/// <summary>圖片燈箱：載入圖片、縮放按鈕與「符合視窗」。</summary>
public class ImageViewerWindowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-viewer-" + Guid.NewGuid().ToString("N"));

    public ImageViewerWindowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string WritePng(int width, int height)
    {
        var path = Path.Combine(_directory, "20261101-100000.png");
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.CornflowerBlue);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void Click(Window window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    // 無頭測試的圖片解碼是 1×1 的替身，所以只驗縮放按鈕的倍率變化，不驗「符合視窗」算出的實際比例
    [AvaloniaFact]
    public void Zoom_buttons_step_the_scale_and_actual_size_resets_to_100_percent()
    {
        var window = new ImageViewerWindow(WritePng(2000, 1000)) { Width = 1000, Height = 700 };
        window.Show();
        var zoom = window.FindControl<TextBlock>("ZoomText")!;
        Assert.Equal("20261101-100000.png", window.FindControl<TextBlock>("NameText")!.Text);

        Click(window, "ActualButton");
        Assert.Equal("100%", zoom.Text);

        Click(window, "ZoomInButton");
        Assert.Equal("125%", zoom.Text);

        Click(window, "ZoomOutButton");
        Click(window, "ZoomOutButton");
        Assert.Equal("80%", zoom.Text);

        Click(window, "ActualButton");
        Assert.Equal("100%", zoom.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Zoom_is_limited_to_a_maximum()
    {
        var window = new ImageViewerWindow(WritePng(200, 100)) { Width = 1000, Height = 700 };
        window.Show();
        for (var i = 0; i < 40; i++)
            Click(window, "ZoomInButton");

        Assert.Equal("1600%", window.FindControl<TextBlock>("ZoomText")!.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Unreadable_file_opens_without_throwing()
    {
        var window = new ImageViewerWindow(Path.Combine(_directory, "gone.png"));
        window.Show();
        window.Close();
    }
}
