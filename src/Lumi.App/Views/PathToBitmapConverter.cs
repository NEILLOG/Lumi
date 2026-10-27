using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Lumi.App.Views;

/// <summary>圖片 chip 的縮圖：從檔案解碼成小圖（只解碼縮圖大小，不把整張圖載入記憶體）。讀不到就回 null，chip 只顯示名稱。</summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    public static readonly PathToBitmapConverter Instance = new();

    private const int ThumbnailWidth = 40;

    /// <summary>寬度預設是 chip 的縮圖（40）；工具卡片的圖片用 <c>ConverterParameter</c> 指定（例如 320）。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
            return null;
        var width = parameter switch
        {
            int number => number,
            string text when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => ThumbnailWidth,
        };
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
