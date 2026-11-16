using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Lumi.App.ViewModels;
using Lumi.Core.ContextNotes;

namespace Lumi.App.Views;

/// <summary>研究本視窗的版本歷史（DataContext 是 <see cref="ContextNoteHistoryViewModel"/>）。</summary>
public partial class ContextNoteHistoryView : UserControl
{
    /// <summary>寬度不到這個值時，版本清單移到差異上方。</summary>
    private const double NarrowWidth = 620;
    private const double NarrowListHeight = 160;
    private const double WideListWidth = 260;

    private bool? _narrow;

    public ContextNoteHistoryView()
    {
        InitializeComponent();
        PropertyChanged += OnViewPropertyChanged;
    }

    private void OnViewPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty || Bounds.Width <= 0)
            return;
        ApplyLayout(Bounds.Width < NarrowWidth);
    }

    /// <summary>窄的時候：一欄，清單在上（高 160）、差異在下；寬的時候：差異在左、清單在右（260）。只在跨越斷點時重排。</summary>
    private void ApplyLayout(bool narrow)
    {
        if (_narrow == narrow)
            return;
        _narrow = narrow;

        BodyGrid.ColumnDefinitions.Clear();
        BodyGrid.RowDefinitions.Clear();
        if (narrow)
        {
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            BodyGrid.RowDefinitions.Add(new RowDefinition(NarrowListHeight, GridUnitType.Pixel));
            BodyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(VersionList, 0);
            Grid.SetColumn(VersionList, 0);
            Grid.SetRow(DiffScroll, 1);
            Grid.SetColumn(DiffScroll, 0);
        }
        else
        {
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            BodyGrid.ColumnDefinitions.Add(new ColumnDefinition(WideListWidth, GridUnitType.Pixel));
            BodyGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetRow(DiffScroll, 0);
            Grid.SetColumn(DiffScroll, 0);
            Grid.SetRow(VersionList, 0);
            Grid.SetColumn(VersionList, 1);
        }
    }

    /// <summary>
    /// 修改的列：把 <see cref="DiffSegment"/> 組成 Run。新增的字用 Success 色加底線（Soft 底的 token 目前沒有）；
    /// 刪除的字用 Danger 淡底加刪除線。沒有字的區段（Same）用一般字。
    /// </summary>
    private void OnChangedTextDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock text)
            return;

        text.Inlines?.Clear();
        if (text.DataContext is not ContextNoteDiffRow { Segments: { } segments })
            return;

        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            switch (segment.Kind)
            {
                case DiffKind.Added:
                    run.Background = FindBrush(text, "LumiSuccessBackgroundBrush");
                    run.TextDecorations = TextDecorations.Underline;
                    break;
                case DiffKind.Removed:
                    run.Foreground = FindBrush(text, "LumiTextMutedBrush");
                    run.Background = FindBrush(text, "LumiDangerBackgroundBrush");
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;
            }
            text.Inlines?.Add(run);
        }
    }

    private static IBrush? FindBrush(Control control, string key) =>
        control.TryFindResource(key, out var value) ? value as IBrush : null;
}
