using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>
/// 回覆附件的排版（M25，ui-spec 3.1）：連續的圖片附件放得下就並排（頂端對齊），放不下換行；
/// 牌陣附件（以及其他非圖片）永遠自成一列。列與列、項目與項目之間都是 <see cref="Gap"/>。
/// 子項目的型別看 <see cref="ItemOf"/>：<c>ItemsControl</c> 的容器是 ContentPresenter，項目在 Content 上。
/// </summary>
public sealed class ReplyAttachmentsPanel : Panel
{
    /// <summary>項目之間的間距（水平、垂直都是 12）。</summary>
    public const double Gap = 12;

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));

        var rows = Rows(availableSize.Width);
        var width = rows.Count == 0 ? 0 : rows.Max(r => r.Width);
        var height = rows.Sum(r => r.Height) + Gap * Math.Max(0, rows.Count - 1);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (var row in Rows(finalSize.Width))
        {
            var x = 0.0;
            foreach (var child in row.Children)
            {
                // 頂端對齊：同一列的項目從同一個 y 開始，寬度與高度用自己的期望尺寸
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width + Gap;
            }
            y += row.Height + Gap;
        }
        return finalSize;
    }

    /// <summary>
    /// 分列：可見的子項目依序排；圖片能接在同一列的最後就接（寬度 ＋ 間距 ≤ 可用寬度），接不上就換列；
    /// 非圖片自成一列，前後都會換列。
    /// </summary>
    private List<Row> Rows(double availableWidth)
    {
        var rows = new List<Row>();
        Row? current = null;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            var size = child.DesiredSize;
            var image = ItemOf(child) is ImageAttachmentViewModel;

            if (image && current is { IsImageRow: true } row && row.Width + Gap + size.Width <= availableWidth)
            {
                row.Add(child, size);
                continue;
            }

            current = new Row(image);
            current.Add(child, size);
            rows.Add(current);
        }
        return rows;
    }

    private static object? ItemOf(Control child) => child is ContentPresenter { Content: { } content } ? content : child.DataContext;

    /// <summary>一列：寬度是各項目寬度加間距，高度是最高的那一個。</summary>
    private sealed class Row(bool isImageRow)
    {
        private readonly List<Control> _children = [];

        public bool IsImageRow { get; } = isImageRow;

        public IReadOnlyList<Control> Children => _children;

        public double Width { get; private set; }

        public double Height { get; private set; }

        public void Add(Control child, Size size)
        {
            Width = _children.Count == 0 ? size.Width : Width + Gap + size.Width;
            Height = Math.Max(Height, size.Height);
            _children.Add(child);
        }
    }
}
