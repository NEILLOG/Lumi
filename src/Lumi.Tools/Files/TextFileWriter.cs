using System.Text;

namespace Lumi.Tools;

/// <summary>文字檔的編碼種類。寫入只支援 UTF-8（含 BOM）與 UTF-16 LE／BE，和 read_file 的範圍一致（M10 計畫）。</summary>
internal enum TextEncodingKind { Utf8, Utf8Bom, Utf16Le, Utf16Be }

/// <summary>既有文字檔的資訊：大小、編碼、換行、行數、結尾是否已換行。</summary>
internal sealed record TextFileInfo(long Size, TextEncodingKind Kind, string Newline, int Lines, bool EndsWithNewline);

/// <summary>
/// 寫入文字檔的共用做法：偵測既有檔案的編碼與換行、新檔一律 UTF-8 無 BOM 與 LF、覆寫用暫存檔再取代（寫到一半失敗時原檔不變）。
/// 所有路徑都必須先經過 <c>context.Scope.Resolve</c>；這裡只負責檔案內容，不解析路徑。
/// </summary>
internal static class TextFileWriter
{
    public const string Lf = "\n";

    public const string CrLf = "\r\n";

    /// <summary>不能由 Lumi 建立或修改的副檔名（複合文件與封存檔；舊版 file_write_text 的名單，不分大小寫）。</summary>
    public static readonly IReadOnlySet<string> BlockedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".doc", ".xlsx", ".xls", ".pdf", ".pptx", ".ppt", ".zip", ".7z", ".rar", ".tar", ".gz",
    };

    // 寫入用的編碼不帶 BOM（BOM 由 Preamble 自己加）；讀取用嚴格解碼
    private static readonly UTF8Encoding Utf8Writer = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly UnicodeEncoding Utf16LeWriter = new(bigEndian: false, byteOrderMark: false);

    private static readonly UnicodeEncoding Utf16BeWriter = new(bigEndian: true, byteOrderMark: false);

    private static readonly UTF8Encoding Utf8Reader = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding Utf16LeReader = new(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);

    private static readonly UnicodeEncoding Utf16BeReader = new(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);

    public static bool IsBlocked(string path, out string extension)
    {
        extension = Path.GetExtension(path);
        return BlockedExtensions.Contains(extension);
    }

    public static string BlockedMessage(string extension) =>
        $"安全限制：不允許建立或修改複合文件與封存檔（副檔名「{extension}」）。";

    /// <summary>工具參數的 encoding：null 或空白是 utf-8（預設）；只接受 utf-8、utf-8-bom、utf-16。</summary>
    public static bool TryParseEncoding(string? name, out TextEncodingKind kind)
    {
        switch (name?.Trim().ToLowerInvariant())
        {
            case null or "" or "utf-8":
                kind = TextEncodingKind.Utf8;
                return true;
            case "utf-8-bom":
                kind = TextEncodingKind.Utf8Bom;
                return true;
            case "utf-16":
                kind = TextEncodingKind.Utf16Le;
                return true;
            default:
                kind = TextEncodingKind.Utf8;
                return false;
        }
    }

    /// <summary>
    /// 行數的算法和 read_file 一致（以 LF 計；最後一行沒有換行也算一行，空檔為 0）。
    /// 文字先正規化，所以 CRLF、LF 與單獨的 CR 數出來的行數相同。
    /// </summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0)
            return 0;
        var normalized = Normalize(text, Lf);
        var lines = normalized.Count(c => c == '\n');
        return normalized[^1] == '\n' ? lines : lines + 1;
    }

    /// <summary>把換行統一成指定的換行字元（<see cref="Lf"/> 或 <see cref="CrLf"/>）。</summary>
    public static string Normalize(string text, string newline)
    {
        var lf = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return newline == Lf ? lf : lf.Replace("\n", newline);
    }

    /// <summary>
    /// 讀既有檔案的編碼、換行與行數。前 8 KB 有 NUL（而且不是 UTF-16 的 BOM）時丟 <see cref="InvalidDataException"/>；
    /// 不合法的 UTF-8 丟 <see cref="DecoderFallbackException"/>（嚴格解碼，不默默換成亂碼）。
    /// 換行以多數決：CRLF 的數量多於單獨的 LF 才用 CRLF，否則 LF。
    /// </summary>
    public static TextFileInfo Inspect(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var size = stream.Length;

        var head = new byte[(int)Math.Min(8192, size)];
        var headRead = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (TextFiles.LooksBinary(head.AsSpan(0, headRead)))
            throw new InvalidDataException("binary");

        var (kind, bomLength) = Sniff(head.AsSpan(0, headRead));
        stream.Position = bomLength;

        using var reader = new StreamReader(stream, DecoderFor(kind), detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var buffer = new char[8192];
        var lf = 0;
        var crlf = 0;
        var newlines = 0;
        var previous = '\0';
        var chars = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    newlines++;
                    if (previous == '\r') crlf++;
                    else lf++;
                }
                previous = c;
            }
            chars += read;
        }

        var endsWithNewline = previous is '\n' or '\r';
        var lines = chars == 0 ? 0 : newlines + (previous == '\n' ? 0 : 1);
        var newline = crlf > lf ? CrLf : Lf;
        return new TextFileInfo(size, kind, newline, lines, endsWithNewline);
    }

    /// <summary>新檔：UTF-8 無 BOM 與 LF 是預設；encoding 參數可指定 UTF-8 BOM 或 UTF-16。檔案已存在就丟 <see cref="IOException"/>。</summary>
    public static void CreateNew(string path, string text, TextEncodingKind kind)
    {
        var bytes = Encode(Normalize(text, Lf), kind);
        WriteNewFile(path, bytes);
    }

    /// <summary>追加到檔案末尾：末行沒有換行就先補一個（用檔案本身的換行），新文字的換行也轉成檔案的換行。</summary>
    public static void Append(string path, string text, TextFileInfo existing)
    {
        var body = Normalize(text, existing.Newline);
        var separator = existing.Size > 0 && !existing.EndsWithNewline ? existing.Newline : "";
        var bytes = EncodeBody(separator + body, existing.Kind);

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }

    /// <summary>覆寫既有檔案：沿用原編碼與換行，先寫同資料夾的暫存檔，再以 File.Move 取代。失敗時清掉暫存檔，原檔不變。</summary>
    public static void Overwrite(string path, string text, TextFileInfo existing)
    {
        var bytes = Encode(Normalize(text, existing.Newline), existing.Kind);
        ReplaceAtomically(path, bytes);
    }

    /// <summary>文字換成指定編碼後的位元組數（含 BOM），給確認卡片顯示大小用。</summary>
    public static long EncodedSize(string text, TextEncodingKind kind) => Encode(text, kind).LongLength;

    /// <summary>刪除檔案；不存在或刪除失敗都不丟例外（清理用）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- 內部 ----

    private static void WriteNewFile(string path, byte[] bytes)
    {
        // CreateNew：已存在就丟 IOException，絕不覆蓋
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            stream.Write(bytes);
            stream.Flush();
        }
        catch
        {
            stream.Dispose();
            TryDelete(path);   // 這個檔案是我們剛建的，寫到一半失敗就清掉，不留半個檔案
            throw;
        }
        stream.Dispose();
    }

    private static void ReplaceAtomically(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteNewFile(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);   // 搬移成功後暫存檔已不存在，這裡什麼都不做
        }
    }

    private static byte[] Encode(string text, TextEncodingKind kind)
    {
        var body = EncodeBody(text, kind);
        return Preamble(kind) is { Length: > 0 } preamble ? [.. preamble, .. body] : body;
    }

    private static byte[] EncodeBody(string text, TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf16Le => Utf16LeWriter.GetBytes(text),
        TextEncodingKind.Utf16Be => Utf16BeWriter.GetBytes(text),
        _ => Utf8Writer.GetBytes(text),
    };

    private static byte[] Preamble(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom => [0xEF, 0xBB, 0xBF],
        TextEncodingKind.Utf16Le => [0xFF, 0xFE],
        TextEncodingKind.Utf16Be => [0xFE, 0xFF],
        _ => [],
    };

    private static (TextEncodingKind Kind, int BomLength) Sniff(ReadOnlySpan<byte> head)
    {
        if (head is [0xEF, 0xBB, 0xBF, ..])
            return (TextEncodingKind.Utf8Bom, 3);
        if (head is [0xFF, 0xFE, ..])
            return (TextEncodingKind.Utf16Le, 2);
        if (head is [0xFE, 0xFF, ..])
            return (TextEncodingKind.Utf16Be, 2);
        return (TextEncodingKind.Utf8, 0);
    }

    private static Encoding DecoderFor(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf16Le => Utf16LeReader,
        TextEncodingKind.Utf16Be => Utf16BeReader,
        _ => Utf8Reader,
    };
}
