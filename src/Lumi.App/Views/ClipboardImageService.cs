using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Lumi.App.ViewModels;
using Lumi.Core.Context;

namespace Lumi.App.Views;

/// <summary>讀取剪貼簿裡的圖片（貼上截圖）。</summary>
public sealed class ClipboardImageService : IClipboardImageService
{
    public async Task<ClipboardImage?> TryGetImageAsync()
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is not { } clipboard)
            return null;

        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null)
            return null;

        // 剪貼簿給的是解碼後的點陣圖：統一編碼成 PNG（無損，貼上的通常是螢幕截圖）
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        var data = stream.ToArray();
        return ImageFormats.Detect(data) is { } mediaType ? new ClipboardImage(data, mediaType) : null;
    }
}
