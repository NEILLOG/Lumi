using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Lumi.App.Views;

/// <summary>
/// 設定首頁（M20 20G）：6 張狀態卡片。行為全部在 <c>HomeViewModel</c> 與 <c>SettingsViewModel.GoToCommand</c>，
/// code-behind 只有 <c>InitializeComponent</c>。
/// </summary>
public partial class HomePanel : UserControl
{
    public HomePanel()
    {
        InitializeComponent();
    }
}

/// <summary>把卡片的圖示 key（例如 <c>LumiIconDiamond</c>）換成資源裡的 <c>StreamGeometry</c>。</summary>
internal sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current is { } app ? app.FindResource(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
