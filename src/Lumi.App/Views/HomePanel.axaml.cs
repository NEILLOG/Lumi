using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 設定首頁（M21 21F）：待處理、重點設定（5 張卡：目前模型、記憶、本月用量、工具、提醒排程）、最近的動作。
/// 資料全部來自 <c>SettingsViewModel.Home</c>（<see cref="HomeViewModel"/>）；切頁用 <c>SettingsViewModel.GoToCommand</c>。
/// code-behind 只負責在頁面顯示時載入用量與最近動作。
/// </summary>
public partial class HomePanel : UserControl
{
    public HomePanel()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is SettingsViewModel settings)
                _ = settings.Home.LoadActivityAsync();
        };
    }
}

/// <summary>把圖示 key（例如 <c>LumiIconDiamond</c>）換成資源裡的 <c>StreamGeometry</c>。</summary>
internal sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current is { } app ? app.FindResource(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>數量大於 0 時為 true（給 <c>IsVisible</c> 用）。</summary>
internal sealed class PositiveCountConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 最近的動作的狀態：parameter 為 <c>icon</c> 時回傳圖示幾何（已允許 Check、失敗 X、你已拒絕 Ban），
/// 其他（<c>brush</c>）回傳顏色。圖示形狀不同，所以不只靠顏色。
/// </summary>
internal sealed class HomeActionStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not HomeActionStatus status || Application.Current is not { } app)
            return null;

        var key = (parameter as string) == "icon"
            ? status switch
            {
                HomeActionStatus.Succeeded => "LumiIconCheck",
                HomeActionStatus.Failed => "LumiIconX",
                _ => "LumiIconBan",
            }
            : status switch
            {
                HomeActionStatus.Succeeded => "LumiSuccessBrush",
                HomeActionStatus.Failed => "LumiDangerBrush",
                _ => "LumiTextMutedBrush",
            };
        return app.FindResource(key);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
