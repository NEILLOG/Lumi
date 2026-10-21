using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Lumi.App.Services;

namespace Lumi.App.Views;

/// <summary>
/// 色票卡片的色塊：輸入色票 ID，參數 Accent／Hover／Soft／Text，輸出該色票自己的顏色（見 <see cref="PalettePreview"/>），
/// 不隨目前主題變。畫筆依顏色快取。
/// </summary>
public sealed class PaletteSwatchConverter : IValueConverter
{
    public static PaletteSwatchConverter Instance { get; } = new();

    private static readonly ConcurrentDictionary<Color, IBrush> Brushes = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string id)
            return null;
        var colors = PalettePreview.For(id);
        var color = (parameter as string) switch
        {
            "Hover" => colors.Hover,
            "Soft" => colors.Soft,
            "Text" => colors.Text,
            _ => colors.Accent,
        };
        return Brushes.GetOrAdd(color, c => new SolidColorBrush(c).ToImmutable());
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
