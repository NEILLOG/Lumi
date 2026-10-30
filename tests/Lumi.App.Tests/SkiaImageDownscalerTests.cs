using Lumi.App.Services;
using SkiaSharp;
using Xunit;

namespace Lumi.App.Tests;

/// <summary>L5：<see cref="SkiaImageDownscaler"/> 的縮圖結果。測試圖片都用 SkiaSharp 現場產生，不放二進位檔案進 repo。</summary>
public class SkiaImageDownscalerTests
{
    private const int MaxEdge = 1568;

    private static byte[] Solid(int width, int height, SKEncodedImageFormat format, int quality = 90)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        bitmap.Erase(new SKColor(30, 120, 200));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    private static SKBitmap Decode(byte[] data)
    {
        var bitmap = SKBitmap.Decode(data);
        Assert.NotNull(bitmap);
        return bitmap;
    }

    /// <summary>在 SOI 後面插入一個 APP1（Exif）區段，把方向設成 <paramref name="orientation"/>（TIFF 小端序，只有一個欄位）。</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,       // II*\0，IFD0 位於 8
            0x01, 0x00,                                           // 1 個欄位
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,       // Orientation（0x0112）、SHORT、1 個
            (byte)orientation, (byte)(orientation >> 8), 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,                               // 下一個 IFD：無
        ];
        var payload = new List<byte> { 0x45, 0x78, 0x69, 0x66, 0x00, 0x00 };   // "Exif\0\0"
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        var app1 = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
        app1.AddRange(payload);

        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    [Fact]
    public void LargePng_IsScaledToLongEdge1568_AndStaysPng()
    {
        var png = Solid(3000, 2000, SKEncodedImageFormat.Png);

        var result = new SkiaImageDownscaler().Downscale(png, "image/png", MaxEdge);

        Assert.NotNull(result);
        Assert.Equal("image/png", result.MediaType);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], result.Data[..4]);
        using var bitmap = Decode(result.Data);
        Assert.Equal(1568, bitmap.Width);
        Assert.Equal(1045, bitmap.Height);
    }

    [Fact]
    public void PortraitImage_IsScaledSoTheHeightIsTheLongEdge()
    {
        var jpeg = Solid(1000, 3000, SKEncodedImageFormat.Jpeg);

        var result = new SkiaImageDownscaler().Downscale(jpeg, "image/jpeg", MaxEdge);

        Assert.NotNull(result);
        using var bitmap = Decode(result.Data);
        Assert.Equal(523, bitmap.Width);
        Assert.Equal(1568, bitmap.Height);
    }

    [Fact]
    public void SmallImage_ReturnsNull()
    {
        var png = Solid(1000, 800, SKEncodedImageFormat.Png);

        Assert.Null(new SkiaImageDownscaler().Downscale(png, "image/png", MaxEdge));
    }

    [Fact]
    public void ImageExactlyAtTheLimit_ReturnsNull()
    {
        var png = Solid(MaxEdge, 100, SKEncodedImageFormat.Png);

        Assert.Null(new SkiaImageDownscaler().Downscale(png, "image/png", MaxEdge));
    }

    [Fact]
    public void Jpeg_IsScaledAndStaysJpeg()
    {
        var jpeg = Solid(3000, 2000, SKEncodedImageFormat.Jpeg);

        var result = new SkiaImageDownscaler().Downscale(jpeg, "image/jpeg", MaxEdge);

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF], result.Data[..3]);
        using var bitmap = Decode(result.Data);
        Assert.Equal(1568, bitmap.Width);
        Assert.Equal(1045, bitmap.Height);
    }

    [Fact]
    public void Webp_IsScaledAndBecomesJpeg()
    {
        var webp = Solid(3000, 2000, SKEncodedImageFormat.Webp, quality: 90);

        var result = new SkiaImageDownscaler().Downscale(webp, "image/webp", MaxEdge);

        Assert.NotNull(result);
        Assert.Equal("image/jpeg", result.MediaType);
        Assert.Equal([0xFF, 0xD8, 0xFF], result.Data[..3]);
    }

    [Fact]
    public void Gif_ReturnsNull_EvenWhenTheBytesAreAnImage()
    {
        var png = Solid(3000, 2000, SKEncodedImageFormat.Png);

        Assert.Null(new SkiaImageDownscaler().Downscale(png, "image/gif", MaxEdge));
    }

    [Fact]
    public void BadData_ReturnsNull()
    {
        byte[] garbage = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Null(new SkiaImageDownscaler().Downscale(garbage, "image/png", MaxEdge));
        Assert.Null(new SkiaImageDownscaler().Downscale([], "image/jpeg", MaxEdge));
    }

    [Fact]
    public void JpegWithRotatedExifOrientation_ReturnsNull_SoTheOriginalKeepsItsRotation()
    {
        var jpeg = WithExifOrientation(Solid(3000, 2000, SKEncodedImageFormat.Jpeg), orientation: 6);

        Assert.Null(new SkiaImageDownscaler().Downscale(jpeg, "image/jpeg", MaxEdge));
    }
}
