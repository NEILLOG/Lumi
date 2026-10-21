using System.Buffers.Binary;

namespace Lumi.Sprites.Assets;

/// <summary>只讀 PNG 的標頭（IHDR）取得寬高，不解碼：用來在解碼前擋掉不合法或過大的圖。</summary>
internal static class PngHeader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <returns>成功回 (寬, 高, null)；不是 PNG 或標頭壞掉回 (0, 0, 原因)。</returns>
    public static (long Width, long Height, string? Error) Read(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[33];
            using var stream = File.OpenRead(path);
            if (stream.Read(head) < head.Length)
                return (0, 0, "不是有效的 PNG（檔案太短）");

            if (!head[..8].SequenceEqual(Signature))
                return (0, 0, "不是有效的 PNG（檔頭不對）");
            if (BinaryPrimitives.ReadUInt32BigEndian(head[8..12]) != 13 || !head[12..16].SequenceEqual("IHDR"u8))
                return (0, 0, "不是有效的 PNG（缺少 IHDR）");

            return (BinaryPrimitives.ReadUInt32BigEndian(head[16..20]), BinaryPrimitives.ReadUInt32BigEndian(head[20..24]), null);
        }
        catch (IOException ex)
        {
            return (0, 0, $"讀不到檔案（{ex.Message}）");
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0, "沒有權限讀取檔案");
        }
    }
}
