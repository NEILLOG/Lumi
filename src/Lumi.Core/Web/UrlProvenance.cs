using System.Text.RegularExpressions;
using Lumi.Core.Chat;

namespace Lumi.Core.Web;

/// <summary>
/// 網址來源（M17 D4）：這一輪對話裡哪些網址「出現過」。
/// 使用者訊息文字裡的網址 → 整個網站都算；使用者加入的檔案內容與工具結果裡的網址 → 只算那個完整網址（忽略 <c>#</c> 之後）。
/// 助理文字、thinking、工具呼叫的引數一律不算：模型自己寫出來的網址不能當作依據。
/// </summary>
public sealed partial class UrlProvenance
{
    private readonly HashSet<string> _origins = new(StringComparer.Ordinal);
    private readonly HashSet<string> _urls = new(StringComparer.Ordinal);

    private UrlProvenance()
    {
    }

    /// <param name="history">這一輪開始時的對話歷史。</param>
    /// <param name="pending">這一輪已經完成、還沒存進歷史的工具結果。</param>
    public static UrlProvenance From(IEnumerable<ChatMessage> history, IEnumerable<ToolResultBlock> pending)
    {
        var provenance = new UrlProvenance();

        foreach (var message in history)
        {
            foreach (var block in message.Content)
            {
                switch (message.Role, block)
                {
                    case (ChatRole.User, TextBlock text):
                        foreach (var url in ExtractUrls(text.Text))
                            provenance._origins.Add(WebOrigin.Of(url));
                        break;
                    case (ChatRole.User, ContextBlock { Content: { } content }):
                        provenance.AddExact(content);
                        break;
                    case (ChatRole.Tool, ToolResultBlock result):
                        provenance.AddExact(result.Content);
                        break;
                }
            }
        }

        foreach (var result in pending)
            provenance.AddExact(result.Content);

        return provenance;
    }

    /// <summary>網站出現在使用者訊息裡，或這個完整網址出現在工具結果與使用者加入的檔案裡。</summary>
    public bool IsKnown(Uri url) =>
        _origins.Contains(WebOrigin.Of(url)) || _urls.Contains(Key(url));

    /// <summary>完整網址的比對鍵：網站＋路徑＋查詢字串（不含 <c>#</c> 之後；路徑與查詢字串區分大小寫）。</summary>
    internal static string Key(Uri url) => WebOrigin.Of(url) + url.PathAndQuery;

    /// <summary>從文字裡找出 http／https 網址：去掉結尾的標點，Markdown 連結 <c>[文字](https://a/b)</c> 取到 <c>https://a/b</c>。</summary>
    internal static IEnumerable<Uri> ExtractUrls(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        foreach (Match match in UrlPattern().Matches(text))
        {
            var candidate = match.Value.TrimEnd(TrailingPunctuation);
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                yield return uri;
        }
    }

    private void AddExact(string? text)
    {
        foreach (var url in ExtractUrls(text))
            _urls.Add(Key(url));
    }

    private static readonly char[] TrailingPunctuation = ['.', ',', ';', ':', '!', '?', '。', '，', '；', '：', '！', '？', '、'];

    [GeneratedRegex(@"https?://[^\s<>""'`\]\)）」』，。；：！？、（《》]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}
