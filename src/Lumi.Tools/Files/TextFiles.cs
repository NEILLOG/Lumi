using System.Text;

namespace Lumi.Tools;

/// <summary>檔案工具共用：以 UTF-8（含 BOM 與 UTF-16 BOM）讀文字檔、判斷二進位檔。</summary>
internal static class TextFiles
{
    public const string BinaryMessage = "這不是文字檔（可能是二進位檔、圖片或其他編碼）。";

    public const string InvalidUtf8Message = "檔案不是有效的 UTF-8 文字（可能是二進位檔或其他編碼）。";

    /// <summary>
    /// 開啟文字檔。前 8 KB 有 NUL 位元組（而且不是 UTF-16/32 的 BOM）就當二進位檔。
    /// 之後讀取時如果遇到不合法的 UTF-8，<see cref="StreamReader"/> 會丟 <see cref="DecoderFallbackException"/>（嚴格解碼，不默默換成亂碼）。
    /// </summary>
    public static StreamReader? Open(string path, out string? error)
    {
        error = null;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var sample = new byte[8192];
            var read = stream.Read(sample, 0, sample.Length);
            if (LooksBinary(sample.AsSpan(0, read)))
            {
                stream.Dispose();
                error = BinaryMessage;
                return null;
            }

            stream.Position = 0;
            return new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            error = e is UnauthorizedAccessException ? "沒有權限讀取這個檔案。" : e is FileNotFoundException or DirectoryNotFoundException ? "找不到檔案。" : "無法讀取這個檔案。";
            return null;
        }
    }

    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.Length >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF)))
            return false;
        return sample.Contains((byte)0);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    public static string ClipLine(string line, int maxChars) =>
        line.Length <= maxChars ? line : line[..maxChars] + "…（此行過長，已截斷）";
}
