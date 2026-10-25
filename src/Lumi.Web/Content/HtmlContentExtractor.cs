using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;
using ReverseMarkdown;
using SmartReader;

namespace Lumi.Web.Content;

/// <summary>HTML 轉 Markdown（M17）：先用 SmartReader 取主要內容，取不到就轉整頁；連結一律轉成絕對網址。不丟例外。</summary>
internal sealed partial class HtmlContentExtractor(ILogger<HtmlContentExtractor> logger) : IWebContentExtractor
{
    private const int LinkTextMaxChars = 200;

    private static readonly string[] NonContentTags = ["script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "embed"];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraNewLines();

    public string DecodeText(byte[] body, string? charset, bool isHtml) => TextDecoder.Decode(body, charset, isHtml);

    public WebPage Extract(string html, Uri baseUrl, bool readable)
    {
        string? title = null;
        IReadOnlyList<WebLink> links = [];
        string plainText = "";
        try
        {
            var parser = new HtmlParser();
            var document = parser.ParseDocument(html);
            title = Collapse(document.Title) is { Length: > 0 } t ? t : null;
            links = CollectLinks(document, baseUrl);
            plainText = PlainText(document);

            var (fragment, articleTitle, usedReadability) = readable ? TryReadability(html, baseUrl) : (null, null, false);
            if (fragment is null)
            {
                foreach (var element in document.QuerySelectorAll(string.Join(',', NonContentTags)).ToList())
                    element.Remove();
                fragment = document.Body?.InnerHtml ?? "";
            }

            var markdown = ToMarkdown(fragment, baseUrl, parser);
            return new WebPage(articleTitle ?? title, markdown, links, usedReadability);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "網頁轉換失敗，退回純文字");
            return new WebPage(title, plainText, links, false);
        }
    }

    /// <summary>SmartReader 取主要內容；取不到（不是文章、例外）時 Fragment 是 null。</summary>
    private (string? Fragment, string? Title, bool Used) TryReadability(string html, Uri baseUrl)
    {
        try
        {
            var article = new Reader(baseUrl.AbsoluteUri, html).GetArticle();
            if (article.IsReadable && !string.IsNullOrWhiteSpace(article.Content))
                return (article.Content, string.IsNullOrWhiteSpace(article.Title) ? null : Collapse(article.Title), true);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "取主要內容失敗，改轉整頁");
        }
        return (null, null, false);
    }

    private static List<WebLink> CollectLinks(IDocument document, Uri baseUrl)
    {
        var links = new List<WebLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (!TryResolve(anchor.GetAttribute("href"), baseUrl, out var url) || !seen.Add(url.AbsoluteUri))
                continue;
            var text = Collapse(anchor.TextContent);
            if (text.Length > LinkTextMaxChars)
                text = text[..LinkTextMaxChars];
            links.Add(new WebLink(text.Length == 0 ? url.AbsoluteUri : text, url));
        }
        return links;
    }

    /// <summary>相對網址轉絕對；只留 http／https，去掉 # 之後。</summary>
    private static bool TryResolve(string? href, Uri baseUrl, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUrl, href.Trim(), out var resolved) || resolved.Scheme is not ("http" or "https"))
            return false;
        url = new Uri(resolved.GetLeftPart(UriPartial.Query));
        return true;
    }

    private static string ToMarkdown(string fragment, Uri baseUrl, HtmlParser parser)
    {
        var document = parser.ParseDocument("<body>" + fragment);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            if (TryResolve(anchor.GetAttribute("href"), baseUrl, out _) && Uri.TryCreate(baseUrl, anchor.GetAttribute("href")!.Trim(), out var absolute))
                anchor.SetAttribute("href", absolute.AbsoluteUri);
        }
        foreach (var image in document.QuerySelectorAll("img").ToList())
        {
            var alt = image.GetAttribute("alt")?.Trim();
            if (string.IsNullOrEmpty(alt))
                image.Remove();
            else
                image.Replace(document.CreateTextNode(alt));
        }

        var config = new Config { GithubFlavored = true };
        config.Tags.Unknown = Config.UnknownTagsOption.Bypass;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        var converter = new Converter(config);
        var markdown = converter.Convert(document.Body?.InnerHtml ?? "");
        return ExtraNewLines().Replace(markdown.Replace("\r\n", "\n"), "\n\n").Trim();
    }

    private static string PlainText(IDocument document)
    {
        foreach (var element in document.QuerySelectorAll(string.Join(',', NonContentTags)).ToList())
            element.Remove();
        return Collapse(document.Body?.TextContent);
    }

    private static string Collapse(string? text) => Whitespace().Replace(text ?? "", " ").Trim();
}
