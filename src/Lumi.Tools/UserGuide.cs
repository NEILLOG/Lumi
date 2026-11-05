using System.Text.RegularExpressions;

namespace Lumi.Tools;

/// <summary>
/// 使用說明的目錄與內文（<c>app_help</c>）。文件是 <c>docs/guide/*.md</c> 打包進組件的內嵌資源（資源名稱 <c>Lumi.Guide.{代號}.md</c>），
/// 第一次用到才讀取，之後快取。
/// </summary>
internal static partial class UserGuide
{
    private const string ResourcePrefix = "Lumi.Guide.";
    private const string ResourceSuffix = ".md";

    private static readonly Lazy<IReadOnlyList<Topic>> Cache = new(Load);

    /// <summary>一篇說明。<c>Id</c> 是檔名去掉 .md，<c>Title</c> 是第一行去掉 <c>#</c>，<c>Summary</c> 是下一個非空白行去掉 <c>&gt;</c>。</summary>
    internal sealed record Topic(string Id, string Title, string Summary, string Body);

    /// <summary>全部說明，依代號的 Ordinal 順序排列。</summary>
    public static IReadOnlyList<Topic> Topics => Cache.Value;

    /// <summary>
    /// 依代號找一篇：不分大小寫，去掉前後空白與結尾的 .md。找不到回 null。
    /// </summary>
    public static Topic? Find(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return null;

        var id = topic.Trim();
        if (id.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
            id = id[..^ResourceSuffix.Length].Trim();

        return Topics.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析一篇文件的標題、摘要與內文。</summary>
    internal static Topic Parse(string id, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        var title = lines.Length > 0 ? lines[0].Trim() : string.Empty;
        if (title.StartsWith("# ", StringComparison.Ordinal))
            title = title[2..].Trim();

        var summary = string.Empty;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var text = line.Trim();
            if (text.StartsWith('>'))
                text = text[1..].Trim();
            summary = text;
            break;
        }

        return new Topic(id, title, summary, markdown);
    }

    /// <summary>去掉圖片語法 <c>![說明](路徑)</c>，一般連結 <c>[文字](網址)</c> 保留。</summary>
    internal static string StripImages(string markdown) => ImagePattern().Replace(markdown, string.Empty);

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImagePattern();

    private static IReadOnlyList<Topic> Load()
    {
        var assembly = typeof(UserGuide).Assembly;
        var topics = new List<Topic>();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                continue;

            var id = name[ResourcePrefix.Length..^ResourceSuffix.Length];
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"找不到內嵌的使用說明：{name}");
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            topics.Add(Parse(id, reader.ReadToEnd()));
        }

        return topics.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }
}
