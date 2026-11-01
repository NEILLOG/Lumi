using Avalonia;
using Avalonia.Controls;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 凱爾特十字的位置對照清單（plan/25 25D；ui-spec 3.1）。一行一個位置，編號照 <see cref="TarotSpreadModel.Cards"/> 的順序 1～10。
/// <see cref="TwoColumns"/> 為 true（外框比內容欄寬，Narrow）時分兩欄：左欄 1～5、右欄 6～10，欄距 12。
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

    static TarotPositionList()
    {
        SpreadProperty.Changed.AddClassHandler<TarotPositionList>((list, _) => list.Rebuild());
        TwoColumnsProperty.Changed.AddClassHandler<TarotPositionList>((list, _) => list.Rebuild());
    }

    public TarotPositionList()
    {
        InitializeComponent();
    }

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

    private void Rebuild()
    {
        Rows.Children.Clear();
        if (Spread is not { } spread)
            return;

        var cards = spread.Cards;
        if (!TwoColumns)
        {
            Rows.Orientation = Avalonia.Layout.Orientation.Vertical;
            Rows.Spacing = RowSpacing;
            for (var i = 0; i < cards.Count; i++)
                Rows.Children.Add(Row(i, cards[i]));
            return;
        }

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

    private static TarotPositionRow Row(int index, TarotPlacedCard card)
    {
        var row = new TarotPositionRow();
        row.Set(index + 1, card);
        return row;
    }
}
