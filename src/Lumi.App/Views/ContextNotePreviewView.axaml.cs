using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>研究本的預覽：七個區段依序，區塊一列（DataContext 是 <see cref="ContextNoteViewModel"/>）。</summary>
public partial class ContextNotePreviewView : UserControl
{
    public ContextNotePreviewView()
    {
        InitializeComponent();
    }

    /// <summary>「原話」切換：展開或收起這個區塊的原話。</summary>
    private void OnQuoteToggleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ContextNoteBlockViewModel block })
            block.IsQuoteOpen = !block.IsQuoteOpen;
    }

    /// <summary>點圖片：用看圖視窗放大（同附件）。</summary>
    private void OnImageTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: ContextNotePart { ImagePath: { } path } })
            return;
        var viewer = new ImageViewerWindow(path, Path.GetFileName(path));
        if (TopLevel.GetTopLevel(this) is Window owner)
            viewer.Show(owner);
        else
            viewer.Show();
    }
}

/// <summary>把圖片的完整路徑換成點陣圖（讀不到回 null，不丟例外）。</summary>
public sealed class ContextNoteImageConverter : IValueConverter
{
    public static readonly ContextNoteImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
            return null;
        try
        {
            return new Bitmap(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
