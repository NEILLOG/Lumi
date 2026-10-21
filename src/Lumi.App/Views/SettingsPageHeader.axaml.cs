using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>
/// 設定頁頁首元件：<see cref="Eyebrow"/>（分類小標）、<see cref="Title"/>（頁面標題 28／700）、
/// <see cref="Description"/>（說明），以及右側放該頁唯一主要動作的 <see cref="Actions"/>（XAML 用 <c>&lt;views:SettingsPageHeader.Actions&gt;</c> 指定；
/// 不能設成內容屬性，因為 UserControl 自己的 XAML 根元素就是 Content）。
/// 每個設定頁的最上方都用它。
/// </summary>
public partial class SettingsPageHeader : UserControl
{
    public static readonly StyledProperty<string?> EyebrowProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Eyebrow));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Description));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SettingsPageHeader, object?>(nameof(Actions));

    public SettingsPageHeader()
    {
        InitializeComponent();
    }

    public string? Eyebrow
    {
        get => GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>頁首右側的主要動作（例如「＋新增連線」按鈕）；每個動作區最多一個 Primary。</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
