using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>點牌放大（ui-spec 3.1）：用看圖視窗開啟合成好的牌面，下方是說明區。</summary>
internal static class TarotCardViewer
{
    /// <summary>100% 時的牌寬：素材原始寬度 1400 的一半，放大到 200% 也還是原圖的解析度。</summary>
    private const double FaceWidth = 700;

    public static ImageViewerWindow Create(TarotPlacedCard card, ITarotCardImages images)
    {
        // 牌面的圖只在這個視窗開著時留著，關閉就釋放
        var bitmaps = new List<Bitmap>();
        Bitmap? Decode(Func<Stream?> open)
        {
            var bitmap = TarotImageDecoder.Decode(open, (int)(FaceWidth * 2));
            if (bitmap is not null)
                bitmaps.Add(bitmap);
            return bitmap;
        }

        var face = new TarotCardFace
        {
            FaceWidth = FaceWidth,
            Card = card,
            Illustration = Decode(() => images.OpenCard(card.CardId)),
            Frame = Decode(images.OpenFrame),
            Window = TarotSpreadView.WindowOf(images),
        };
        face.Render();

        return new ImageViewerWindow(
            face,
            new Size(FaceWidth, TarotCardFaceMetrics.Height(FaceWidth)),
            TarotText.Caption(card),
            new TarotCardDetailsView(card),
            () => bitmaps.ForEach(b => b.Dispose()))
        {
            Width = 640,
            Height = 820,
        };
    }

    /// <summary>從 <paramref name="source"/> 所在的視窗開啟（關閉那個視窗時一起關閉，釘選的牌陣視窗也蓋不住它）。</summary>
    public static void Open(TarotPlacedCard card, Control source, ITarotCardImages images)
    {
        var viewer = Create(card, images);
        if (TopLevel.GetTopLevel(source) is Window owner)
            viewer.Show(owner);
        else
            viewer.Show();
    }
}
