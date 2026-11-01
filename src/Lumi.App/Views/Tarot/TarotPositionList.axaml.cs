using Avalonia;
using Avalonia.Controls;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 凱爾特十字的位置對照清單（plan/25 25D、25F；ui-spec 3.1）。一行一個位置，編號照 <see cref="TarotSpreadModel.Cards"/> 的順序 1～10。
/// <see cref="TwoColumns"/> 為 true（外框比內容欄寬，Narrow；或牌陣視窗）時分兩欄：左欄 1～5、右欄 6～10，欄距 12。
/// <see cref="Interactive"/>（牌陣視窗）時行有內距與圓角，<see cref="HighlightedIndex"/> 讓對應的行加高亮底，滑鼠進出行會發 <see cref="RowHovered"/>。
/// </summary>
public partial class TarotPositionList : UserControl
{
    /// <summary>欄距（ui-spec 3.1：兩欄時 12）。</summary>
    private const double ColumnSpacing = 12;

    /// <summary>行距（ui-spec 3.1：4）。</summary>
    private const double RowSpacing = 4;

    public static readonly StyledProperty<TarotSpreadModel?> SpreadProperty =
        AvaloniaProperty.Register<TarotPositionList, TarotSpreadModel?>(nameof(Spread));

    public static readonly StyledProperty<bool> TwoColumnsProperty =
        AvaloniaProperty.Register<TarotPositionList, bool>(nameof(TwoColumns));

    public static readonly StyledProperty<bool> InteractiveProperty =
        AvaloniaProperty.Register<TarotPositionList, bool>(nameof(Interactive));

    /// <summary>高亮的編號（1～10）；null 表示沒有高亮。只改行的底色，不重建。</summary>
    public static readonly StyledProperty<int?> HighlightedIndexProperty =
        AvaloniaProperty.Register<TarotPositionList, int?>(nameof(HighlightedIndex));

    private readonly List<TarotPositionRow> _rows = [];

    static TarotPositionList()
    {
        SpreadProperty.Changed.AddClassHandler<TarotPositionList>((list, _) => list.Rebuild());
        TwoColumnsProperty.Changed.AddClassHandler<TarotPositionList>((list, _) => list.Rebuild());
        InteractiveProperty.Changed.AddClassHandler<TarotPositionList>((list, _) => list.Rebuild());
        HighlightedIndexProperty.Changed.AddClassHandler<TarotPositionList>((list, _) => list.ApplyHighlight());
    }

    public TarotPositionList()
    {
        InitializeComponent();
    }

    /// <summary>滑鼠進入某一行時給它的編號（1～10）；離開時給 null。</summary>
    public event EventHandler<int?>? RowHovered;

    public TarotSpreadModel? Spread
    {
        get => GetValue(SpreadProperty);
        set => SetValue(SpreadProperty, value);
    }

    /// <summary>true 時兩欄（左 1～5、右 6～10）。</summary>
    public bool TwoColumns
    {
        get => GetValue(TwoColumnsProperty);
        set => SetValue(TwoColumnsProperty, value);
    }

    /// <summary>互動模式（牌陣視窗）：行有內距與圓角，可以高亮。預設 false，對話內縮小版的外觀不變。</summary>
    public bool Interactive
    {
        get => GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    /// <summary>高亮的編號（1～10）；null 表示沒有高亮。</summary>
    public int? HighlightedIndex
    {
        get => GetValue(HighlightedIndexProperty);
        set => SetValue(HighlightedIndexProperty, value);
    }

    private void Rebuild()
    {
        Rows.Children.Clear();
        _rows.Clear();
        if (Spread is not { } spread)
            return;

        var cards = spread.Cards;
        if (!TwoColumns)
        {
            Rows.Orientation = Avalonia.Layout.Orientation.Vertical;
            Rows.Spacing = RowSpacing;
            for (var i = 0; i < cards.Count; i++)
                Rows.Children.Add(Row(i, cards[i]));
        }
        else
        {
            // 左欄放前半（5 張），右欄放後半
            Rows.Orientation = Avalonia.Layout.Orientation.Horizontal;
            Rows.Spacing = ColumnSpacing;
            var half = (cards.Count + 1) / 2;
            var left = new StackPanel { Spacing = RowSpacing };
            var right = new StackPanel { Spacing = RowSpacing };
            for (var i = 0; i < cards.Count; i++)
                (i < half ? left : right).Children.Add(Row(i, cards[i]));
            Rows.Children.Add(left);
            Rows.Children.Add(right);
        }
        ApplyHighlight();
    }

    private TarotPositionRow Row(int index, TarotPlacedCard card)
    {
        var row = new TarotPositionRow { Interactive = Interactive };
        row.Set(index + 1, card);
        row.PointerEntered += (_, _) => RowHovered?.Invoke(this, row.PositionNumber);
        row.PointerExited += (_, _) => RowHovered?.Invoke(this, null);
        _rows.Add(row);
        return row;
    }

    /// <summary>依 <see cref="HighlightedIndex"/> 更新每一行的高亮。</summary>
    private void ApplyHighlight()
    {
        foreach (var row in _rows)
            row.Highlighted = HighlightedIndex == row.PositionNumber;
    }
}
