using Lumi.Core.Usage;
using SkiaSharp;

namespace Lumi.App.Services;

/// <summary>
/// 用 SkiaSharp 把加入上下文的大圖縮到長邊上限（L5）。PNG 維持 PNG；其他（JPEG、WebP）輸出 JPEG 品質 85。
/// GIF 不處理（可能是動畫）。解不開、不需要縮、或帶 EXIF 方向（縮圖會丟掉方向資訊，照片會轉錯）都回 null，呼叫端用原圖。
/// </summary>
public sealed class SkiaImageDownscaler : IImageDownscaler
{
    private const int JpegQuality = 85;

    public DownscaledImage? Downscale(byte[] data, string mediaType, int maxEdge)
    {
        if (mediaType == "image/gif")
            return null;

        using var encodedData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encodedData);
        if (codec is null || codec.EncodedOrigin != SKEncodedOrigin.TopLeft)
            return null;

        var info = codec.Info;
        var longEdge = Math.Max(info.Width, info.Height);
        if (longEdge <= maxEdge)
            return null;

        // 長邊正好等於上限，短邊等比例（四捨五入）
        var (width, height) = info.Width >= info.Height
            ? (maxEdge, (int)Math.Max(1, Math.Round((double)info.Height * maxEdge / info.Width)))
            : ((int)Math.Max(1, Math.Round((double)info.Width * maxEdge / info.Height)), maxEdge);

        using var source = SKBitmap.Decode(codec);
        if (source is null)
            return null;

        using var resized = source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), SKFilterQuality.High);
        if (resized is null)
            return null;

        using var image = SKImage.FromBitmap(resized);
        var png = mediaType == "image/png";
        using var encoded = png
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        if (encoded is null)
            return null;

        return new DownscaledImage(encoded.ToArray(), png ? "image/png" : "image/jpeg");
    }
}
