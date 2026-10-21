using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Views;

/// <summary>供應商字首方塊：Soft 底、字首字元（連線格式／名稱決定，見 <c>ProviderInitial</c>），不用官方商標。</summary>
public partial class ProviderMark : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ProviderMark, string?>(nameof(Text));

    public ProviderMark()
    {
        InitializeComponent();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}
