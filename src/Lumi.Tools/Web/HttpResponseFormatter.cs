using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Lumi.Core.Tools;
using Lumi.Core.Web;

namespace Lumi.Tools.Web;

/// <summary>把 HTTP 回應排成給模型看的文字：狀態行、主要標頭、內容（JSON 排版、文字原文、二進位只寫大小）。</summary>
internal static class HttpResponseFormatter
{
    private static readonly string[] ShownHeaders =
        ["content-type", "content-length", "location", "last-modified", "etag", "retry-after", "www-authenticate"];

    private static readonly JsonSerializerOptions IndentOptions = new()
    {
        WriteIndented = true,
        // 預設會把中文跳脫成 \uXXXX，排版後難讀
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>狀態碼 ≥ 400 時 <see cref="ToolResult.IsError"/> 為 true，內容仍照常給模型（例如錯誤頁的訊息）。</summary>
    internal static ToolResult Format(WebResponse response, string method, int? maxChars)
    {
        var lines = new List<string> { $"HTTP {response.StatusCode} {response.ReasonPhrase}" };
        foreach (var name in ShownHeaders)
        {
            var header = response.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
            if (header.Key is not null)
                lines.Add($"{name}: {header.Value}");
        }

        var head = string.Join("\n", lines);
        var content = method == "HEAD" ? head : head + "\n\n" + Body(response, maxChars);
        var wrapped = WebToolSupport.Wrap("web_response", response.FinalUrl, null, WebToolSupport.ResponseNotice, null, content);
        return new ToolResult(response.StatusCode >= 400, wrapped, $"HTTP {response.StatusCode}");
    }

    private static string Body(WebResponse response, int? maxChars)
    {
        string text;
        if (response.Body.Length == 0)
            text = "（沒有內容）";
        else if (IsJson(response.MediaType))
            text = PrettyJson(Decode(response.Body, response.Charset));
        else if (IsText(response.MediaType))
            text = Decode(response.Body, response.Charset);
        else
            text = $"（二進位內容，{TextFiles.FormatSize(response.Body.Length)}，未顯示）";

        var (shown, _, _, next) = WebToolSupport.Page(text, 0, maxChars);
        var builder = new StringBuilder(shown);
        if (next is not null)
            builder.Append($"\n（內容很長，只顯示前 {shown.Length:N0} 字。）");
        if (response.Truncated)
            builder.Append("\n（內容超過上限，只讀了一部分。）");
        return builder.ToString();
    }

    /// <summary>解碼：<paramref name="charset"/> 有值時用它，認不得或沒有就用 UTF-8（API 幾乎都是 UTF-8，不另外支援 Big5）。</summary>
    private static string Decode(byte[] body, string? charset)
    {
        Encoding encoding;
        try
        {
            encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            encoding = Encoding.UTF8;
        }
        return encoding.GetString(body);
    }

    /// <summary>不是合法 JSON 時照原文回傳。</summary>
    private static string PrettyJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement, IndentOptions);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private static bool IsJson(string? mediaType) =>
        mediaType is "application/json" || (mediaType?.EndsWith("+json", StringComparison.Ordinal) ?? false);

    private static bool IsText(string? mediaType) =>
        mediaType is not null
        && (mediaType.StartsWith("text/", StringComparison.Ordinal)
            || mediaType is "application/xml"
            || mediaType.EndsWith("+xml", StringComparison.Ordinal));
}
