using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using AngleSharp.Html.Parser;
using Lumi.Core.Web;

namespace Lumi.Web.Content;

/// <summary>RSS 2.0／Atom 解析（M17）。禁止 DTD、不解析外部實體，任何問題都回 null。</summary>
internal sealed partial class FeedReader : IFeedReader
{
    private const int SummaryMaxChars = 300;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public Feed? Parse(byte[] body)
    {
        try
        {
            using var stream = new MemoryStream(body);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var feed = SyndicationFeed.Load(xml);
            if (feed is null)
                return null;

            var baseLink = feed.Links.Select(l => l.Uri).FirstOrDefault(u => u is { IsAbsoluteUri: true });
            var items = feed.Items.Select(i => ToItem(i, baseLink)).ToList();
            return new Feed(feed.Title?.Text is { Length: > 0 } title ? title : "（沒有標題）", items);
        }
        catch (Exception e) when (e is XmlException or ArgumentException or InvalidOperationException or FormatException or UriFormatException or IOException)
        {
            return null;
        }
    }

    private static FeedItem ToItem(SyndicationItem item, Uri? baseLink)
    {
        var link = item.Links.Select(l => l.Uri).FirstOrDefault(u => u is not null);
        if (link is { IsAbsoluteUri: false })
            link = baseLink is null ? null : new Uri(baseLink, link);

        DateTimeOffset? published = item.PublishDate != DateTimeOffset.MinValue ? item.PublishDate
            : item.LastUpdatedTime != DateTimeOffset.MinValue ? item.LastUpdatedTime
            : null;

        var summary = item.Summary?.Text ?? (item.Content as TextSyndicationContent)?.Text ?? "";
        return new FeedItem(item.Title?.Text is { Length: > 0 } title ? title : "（沒有標題）", link, published, PlainText(summary));
    }

    /// <summary>去掉 HTML 標籤、合併空白、最多 300 字（超過加「…」）。</summary>
    private static string PlainText(string html)
    {
        if (html.Length == 0)
            return "";
        var text = new HtmlParser().ParseDocument("<body>" + html).Body?.TextContent ?? "";
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length > SummaryMaxChars ? text[..SummaryMaxChars] + "…" : text;
    }
}
