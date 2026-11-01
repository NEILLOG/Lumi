using Avalonia.Controls;

namespace Lumi.App.Views.Tarot;

/// <summary>牌下方的位置名稱行（plan/25 25D）。</summary>
public partial class TarotCaption : UserControl
{
    public TarotCaption()
    {
        InitializeComponent();
    }

    /// <summary>填入這張牌的位置名稱與牌名；完整文字放在提示裡。</summary>
    public void Set(Lumi.Tarot.TarotPlacedCard card)
    {
        var (position, tail) = TarotText.CaptionParts(card);
        Head.Text = position;
        Tail.Text = tail;
        ToolTip.SetTip(this, TarotText.Caption(card));
    }
}
