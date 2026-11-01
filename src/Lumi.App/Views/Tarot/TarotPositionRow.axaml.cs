using Avalonia.Controls;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>位置對照清單的一行（plan/25 25D）。</summary>
public partial class TarotPositionRow : UserControl
{
    public TarotPositionRow()
    {
        InitializeComponent();
    }

    public void Set(int number, TarotPlacedCard card)
    {
        Number.Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (position, tail) = TarotText.CaptionParts(card);
        Head.Text = position;
        Tail.Text = tail;
    }
}
