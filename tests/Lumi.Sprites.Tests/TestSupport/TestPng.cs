using System.Buffers.Binary;
using System.IO.Compression;

namespace Lumi.Sprites.Tests.TestSupport;

/// <summary>測試用的 PNG：自己寫（zlib＋CRC），不依賴任何影像程式庫，也不依賴使用者的美術。</summary>
internal static class TestPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>RGBA、8 位元的 PNG。<paramref name="pixel"/> 回傳 0xAARRGGBB。</summary>
    public static byte[] Create(int width, int height, Func<int, int, uint> pixel)
    {
        var raw = new byte[(width * 4 + 1) * height];
        var i = 0;
        for (var y = 0; y < height; y++)
        {
            raw[i++] = 0;   // filter: none
            for (var x = 0; x < width; x++)
            {
                var argb = pixel(x, y);
                raw[i++] = (byte)(argb >> 16);
                raw[i++] = (byte)(argb >> 8);
                raw[i++] = (byte)argb;
                raw[i++] = (byte)(argb >> 24);
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;    // bit depth
        header[9] = 6;    // RGBA
        Chunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>每一格一個顏色（左上角 8×8 以外透明），方便測試切格：第 n 格的顏色是 <see cref="FrameColor"/>。</summary>
    public static byte[] Sheet(int cellWidth, int cellHeight, int frames, int columns)
    {
        var rows = (frames + columns - 1) / columns;
        return Create(cellWidth * columns, cellHeight * rows, (x, y) =>
        {
            var index = y / cellHeight * columns + x / cellWidth;
            if (index >= frames)
                return 0;
            var inside = x % cellWidth >= cellWidth / 4 && x % cellWidth < cellWidth * 3 / 4 && y % cellHeight >= cellHeight / 4 && y % cellHeight < cellHeight * 3 / 4;
            return inside ? FrameColor(index) : 0;
        });
    }

    public static uint FrameColor(int index) => 0xFF000000u | (uint)((index * 37 + 40) & 0xFF) << 16 | (uint)((index * 71 + 90) & 0xFF) << 8 | (uint)((index * 13 + 200) & 0xFF);

    /// <summary>只有簽章與 IHDR（宣告的寬高可以很大）：用來測「在解碼前就擋下來」。</summary>
    public static byte[] HeaderOnly(uint width, uint height)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var body = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(body, 0);
        data.CopyTo(body, 4);
        output.Write(body);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
        output.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
