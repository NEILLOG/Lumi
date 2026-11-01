using Avalonia.Controls;
using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>點牌放大的說明區（ui-spec 3.1）。資料都來自 tarot_draw 的結果；沒有的欄位整列不顯示。</summary>
public partial class TarotCardDetailsView : UserControl
{
    public TarotCardDetailsView() : this(null) { }

    public TarotCardDetailsView(TarotPlacedCard? card)
    {
        InitializeComponent();
        if (card is null)
            return;
        PositionText.Text = card.PositionName;
        PositionMeaningText.Text = card.PositionMeaning;
        NameText.Text = card.CardNameZh;
        SubtitleText.Text = TarotText.Subtitle(card);
        OrientationText.Text = card.IsReversed ? Strings.Tarot_Reversed : Strings.Tarot_Upright;
        KeywordList.ItemsSource = card.Keywords;
        KeywordList.IsVisible = card.Keywords.Count > 0;
        MeaningText.Text = card.Meaning;
        MeaningText.IsVisible = card.Meaning.Length > 0;
    }
}
