using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Speech.Tests.TestSupport;

/// <summary>極簡的 multipart/form-data 解析，只用來檢查我們送出的表單。文字以 Latin-1 讀，二進位內容（WAV）才不會被改壞。</summary>
internal sealed record MultipartForm(IReadOnlyList<FormPart> Parts)
{
    public static MultipartForm Parse(byte[] body, string contentType)
    {
        var boundary = contentType.Split("boundary=")[1].Trim().Trim('"');
        var text = Encoding.Latin1.GetString(body);
        var parts = new List<FormPart>();

        foreach (var raw in text.Split("--" + boundary))
        {
            // 最前面是空的，最後面是 "--"
            if (raw.Length == 0 || raw.StartsWith("--", StringComparison.Ordinal))
                continue;

            var section = raw.StartsWith("\r\n", StringComparison.Ordinal) ? raw[2..] : raw;
            var split = section.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headers = section[..split];
            var value = section[(split + 4)..];
            if (value.EndsWith("\r\n", StringComparison.Ordinal))
                value = value[..^2];

            var disposition = headers.Split("\r\n").First(h => h.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase));
            var name = Param(disposition, "name")!;
            var fileName = Param(disposition, "filename");
            var partType = headers.Split("\r\n").FirstOrDefault(h => h.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase))?["Content-Type:".Length..].Trim();
            parts.Add(new FormPart(name, fileName, partType, value));
        }

        return new MultipartForm(parts);
    }

    public bool Has(string name) => Parts.Any(p => p.Name == name);

    /// <summary>文字欄位的值；沒有時 null。</summary>
    public string? Value(string name) => Parts.FirstOrDefault(p => p.Name == name)?.Value;

    public FormPart File => Parts.Single(p => p.Name == "file");

    /// <summary>取出 Content-Disposition 的參數；.NET 對簡單的值不加引號（name=file），有引號的也要認得。</summary>
    private static string? Param(string disposition, string name)
    {
        var match = Regex.Match(disposition, $"(?<![\\w]){name}=(?:\"([^\"]*)\"|([^;\\s]+))");
        return match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }
}

internal sealed record FormPart(string Name, string? FileName, string? ContentType, string Value);
