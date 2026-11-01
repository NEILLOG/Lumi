using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Lumi.App.Resources;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 一張塔羅牌的牌面（plan/25 D10、D10b）。三層：插畫（逆位只轉這層）、邊框（有範本用圖，否則金框）、
/// 文字（圓章編號、牌名板的中文名、逆位角標；英文名放在牌陣圖的滑鼠提示）。任一層缺素材不影響其他層。
/// 呼叫端先設定屬性，再呼叫 <see cref="Render"/>；圖片由呼叫端解碼並負責釋放。
/// </summary>
public partial class TarotCardFace : UserControl
{
    public TarotCardFace()
    {
        InitializeComponent();
    }

    /// <summary>牌寬（像素）；牌高 = 牌寬 × 12/7。</summary>
    public double FaceWidth { get; set; } = 72;

    /// <summary>要畫的牌。</summary>
    public TarotPlacedCard? Card { get; set; }

    /// <summary>插畫（已縮小解碼）；null 時用花色色塊。</summary>
    public Bitmap? Illustration { get; set; }

    /// <summary>共用的邊框範本（已縮小解碼）；null 時用簡易金框。</summary>
    public Bitmap? Frame { get; set; }

    /// <summary>邊框窗口（插畫的旋轉中心 Y 取自這裡）。</summary>
    public TarotWindow Window { get; set; } = TarotWindow.Default;

    /// <summary>依目前的屬性排版並填入內容。沒有牌時什麼都不做。</summary>
    public void Render()
    {
        if (Card is not { } card)
            return;

        var m = TarotCardFaceMetrics.Compute(FaceWidth);
        Width = m.Width;
        Height = m.Height;

        // 插畫層：有圖用圖，否則花色色塊；逆位只旋轉這一層（以窗口中心為旋轉中心，D10）
        if (Illustration is { } illustration)
        {
            IllustrationImage.Source = illustration;
            IllustrationImage.IsVisible = true;
        }
        else
        {
            SuitBlock.Background = BrushOrTransparent(TarotCardFaceMetrics.SuitBrushKey(card.CardId));
            SuitBlock.IsVisible = true;
        }
        IllustrationLayer.RenderTransformOrigin = new RelativePoint(0.5, Window.CenterY, RelativeUnit.Relative);
        IllustrationLayer.RenderTransform = card.IsReversed ? new RotateTransform(180) : null;

        // 邊框層：有範本用圖，否則簡易金框（D10b）
        if (Frame is { } frame)
        {
            FrameImage.Source = frame;
            FrameImage.IsVisible = true;
        }
        else
        {
            GoldFrame.BorderThickness = new Thickness(m.FrameStroke);
            GoldFrame.CornerRadius = new CornerRadius(m.FrameCornerRadius);
            GoldFrame.IsVisible = true;
        }

        // 文字層：圓章編號、牌名板（中文名）、逆位角標。有範本時只寫字；沒有範本時自己畫圓章與牌名板（D10b）
        var plateBackground = Frame is null ? BrushOrTransparent("LumiTarotPlateBrush") : Brushes.Transparent;
        var plateBorder = Frame is null ? BrushOrTransparent("LumiTarotGoldBrush") : Brushes.Transparent;
        Seal.Background = plateBackground;
        Seal.BorderBrush = plateBorder;
        Plate.Background = plateBackground;
        Plate.BorderBrush = plateBorder;
        Plate.BorderThickness = new Thickness(Frame is null ? 1 : 0);
        Plate.CornerRadius = new CornerRadius(Frame is null ? 2 : 0);
        Seal.Width = m.SealDiameter;
        Seal.Height = m.SealDiameter;
        Seal.CornerRadius = new CornerRadius(m.SealDiameter / 2);
        Seal.BorderThickness = new Thickness(m.SealStroke);
        Canvas.SetLeft(Seal, (m.Width - m.SealDiameter) / 2);
        Canvas.SetTop(Seal, m.SealTop);
        SealText.Text = TarotCardFaceMetrics.Number(card.CardId) ?? "";
        SealText.FontSize = m.SealFontSize;

        Canvas.SetLeft(Plate, m.PlateLeft);
        Canvas.SetTop(Plate, m.PlateTop);
        Plate.Width = m.PlateWidth;
        Plate.Height = m.PlateHeight;
        NameZh.Text = card.CardNameZh;
        NameZh.FontSize = m.NameZhFontSize;
        // 角標佔牌名板右端；逆位時名稱讓出這一格，避免文字壓到角標
        NameZh.Margin = new Thickness(card.IsReversed ? m.BadgeSize : 0, 0, card.IsReversed ? m.BadgeSize : 0, 0);

        ReversedBadge.IsVisible = card.IsReversed;
        if (card.IsReversed)
        {
            Canvas.SetLeft(ReversedBadge, m.BadgeLeft);
            Canvas.SetTop(ReversedBadge, m.BadgeTop);
            ReversedBadge.Width = m.BadgeSize;
            ReversedBadge.Height = m.BadgeSize;
            ReversedBadge.CornerRadius = new CornerRadius(m.BadgeSize / 4);
            ReversedText.Text = Strings.Tarot_ReversedBadge;
            ReversedText.FontSize = m.BadgeFontSize;
        }
    }

    /// <summary>找不到 token（例如還沒接上視覺樹）時退回透明，不丟例外。</summary>
    private IBrush BrushOrTransparent(string key) =>
        TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Transparent;
}
