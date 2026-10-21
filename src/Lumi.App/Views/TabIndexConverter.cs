using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Lumi.App.Views;

/// <summary>
/// 分段切換用：整數索引（<c>SettingsViewModel.TabIndex</c>）與「這一段有沒有被選取」雙向轉換。
/// ConverterParameter 是這一段的索引；被取消選取（false）時不回寫，只有被選取的那一段會改索引。
/// </summary>
public sealed class TabIndexConverter : IValueConverter
{
    public static readonly TabIndexConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index && TryParse(parameter, out var segment) && index == segment;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && TryParse(parameter, out var segment) ? segment : BindingOperations.DoNothing;

    private static bool TryParse(object? parameter, out int segment) =>
        int.TryParse(parameter?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out segment);
}
