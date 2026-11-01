using Avalonia.Media.Imaging;

namespace Lumi.App.Views.Tarot;

/// <summary>牌面素材的解碼（牌陣與點牌放大共用）。</summary>
internal static class TarotImageDecoder
{
    /// <summary>
    /// 用指定寬度解碼（D6b）。任何例外都回 null，由呼叫端退回替代牌面。串流用完即釋放。
    /// 先檢查檔頭：Avalonia 遇到不是圖片的位元組不會丟例外，而是回一張空白圖，所以不檢查就會變成空白牌面。
    /// </summary>
    public static Bitmap? Decode(Func<Stream?> open, int width)
    {
        try
        {
            using var source = open();
            if (source is null)
                return null;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (!HasImageSignature(bytes))
                return null;

            using var decodeStream = new MemoryStream(bytes);
            var bitmap = Bitmap.DecodeToWidth(decodeStream, width);
            if (bitmap.PixelSize.Width <= 0 || bitmap.PixelSize.Height <= 0)
            {
                bitmap.Dispose();
                return null;
            }
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>PNG、JPEG、WebP 的檔頭；其他位元組一律視為壞圖。</summary>
    private static bool HasImageSignature(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return true;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return true;
        return bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
    }
}
