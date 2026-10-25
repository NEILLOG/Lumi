using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Web.Content;

/// <summary>位元組轉文字：BOM → Content-Type 的 charset → （HTML 才看）meta charset → UTF-8。</summary>
internal static partial class TextDecoder
{
    /// <summary>找 meta charset 時最多看前幾個位元組。</summary>
    private const int MetaScanBytes = 4096;

    static TextDecoder()
    {
        // 讓 Big5、GBK、Shift_JIS 等舊編碼可以用名稱取得
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [GeneratedRegex("""<meta[^>]*?charset\s*=\s*["']?\s*([A-Za-z0-9_:.\-]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetaCharset();

    public static string Decode(byte[] body, string? charset, bool isHtml)
    {
        if (body.Length == 0)
            return "";

        if (body is [0xEF, 0xBB, 0xBF, ..])
            return Encoding.UTF8.GetString(body, 3, body.Length - 3);
        if (body is [0xFF, 0xFE, ..])
            return Encoding.Unicode.GetString(body, 2, body.Length - 2);
        if (body is [0xFE, 0xFF, ..])
            return Encoding.BigEndianUnicode.GetString(body, 2, body.Length - 2);

        var encoding = Find(charset);
        if (encoding is null && isHtml)
        {
            var head = Encoding.Latin1.GetString(body, 0, Math.Min(body.Length, MetaScanBytes));
            var match = MetaCharset().Match(head);
            if (match.Success)
                encoding = Find(match.Groups[1].Value);
        }

        return (encoding ?? Encoding.UTF8).GetString(body);
    }

    private static Encoding? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
