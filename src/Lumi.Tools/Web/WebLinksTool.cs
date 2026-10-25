using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Lumi.Core.Tools;
using Lumi.Core.Web;

namespace Lumi.Tools.Web;

public sealed record WebLinksArgs(
    [property: Description("要列出連結的網頁網址（http 或 https）")] string Url,
    [property: Description("只列出文字或網址包含這段文字的連結（不分大小寫）")] string? Filter = null,
    [property: Description("true＝只列出和這個網頁同一個網站（主機）的連結")] bool? SameSite = null,
    [property: Description("最多列出幾個，預設 50，範圍 1 到 200")] int? Limit = null);

/// <summary>列出網頁上的連結（M17 17E），用來找出下一個要讀的頁面。</summary>
public sealed class WebLinksTool(IWebClient web, IWebContentExtractor extractor) : ToolBase<WebLinksArgs>
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;
    private const int MaxTextLength = 80;

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    public override ToolDescriptor Descriptor { get; } = new(
        "web_links", "列出網頁連結",
        "列出一個網頁上的連結（文字與完整網址），可以用 filter 篩選、same_site 只留同一個網站的連結。用來找出下一個要讀的頁面；這裡列出的網址之後用 web_fetch 讀取時不需要再確認網站。" +
        "規則同 web_fetch：只能讀使用者給的或之前結果裡出現過的網址。",
        ToolPermission.Network, ToolModes.Both, TimeSpan.FromSeconds(60), Group: ToolGroups.Web, Summary: "列出網頁上的連結。");

    protected override ToolPreview Describe(WebLinksArgs args, ToolContext context) => WebToolSupport.Preview(args.Url);

    protected override async Task<ToolResult> InvokeAsync(WebLinksArgs args, ToolContext context, CancellationToken ct)
    {
        if (!WebToolSupport.TryParseUrl(args.Url, out var url, out var error))
            return ToolResult.Error(error, "參數不正確");

        WebResponse response;
        try
        {
            response = await web.SendAsync(new WebRequest(url) { MaxBytes = WebLimits.PageMaxBytes }, ct);
        }
        catch (WebRequestException e)
        {
            return WebToolSupport.Failure(e);
        }

        if (response.StatusCode >= 400)
            return WebToolSupport.HttpError(response);

        if (response.MediaType is not ("text/html" or "application/xhtml+xml"))
            return ToolResult.Error("這個網址不是網頁，沒有連結可以列出。", "不是網頁");

        var html = extractor.DecodeText(response.Body, response.Charset, true);
        var page = extractor.Extract(html, response.FinalUrl, readable: false);

        var filter = args.Filter?.Trim() ?? "";
        var links = page.Links.AsEnumerable();
        if (filter.Length > 0)
            links = links.Where(l => l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase) || l.Url.AbsoluteUri.Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (args.SameSite == true)
            links = links.Where(l => string.Equals(l.Url.Host, response.FinalUrl.Host, StringComparison.OrdinalIgnoreCase));

        var matched = links.ToList();
        var limit = Math.Clamp(args.Limit ?? DefaultLimit, 1, MaxLimit);
        var listed = matched.Take(limit).ToList();

        var content = listed.Count == 0
            ? "（沒有符合的連結。）"
            : string.Join("\n", listed.Select(l => $"- [{LinkText(l.Text)}]({l.Url.AbsoluteUri})"));
        var meta = $"共 {matched.Count.ToString("N0", CultureInfo.InvariantCulture)} 個連結，列出 {listed.Count.ToString("N0", CultureInfo.InvariantCulture)} 個。";

        var wrapped = WebToolSupport.Wrap("web_content", response.FinalUrl, page.Title, WebToolSupport.ContentNotice, meta, content);
        return ToolResult.Ok(wrapped, $"{listed.Count} 個連結");
    }

    /// <summary>連結文字：合併空白、最多 80 字，方括號換成全形，免得破壞 Markdown 連結的格式。</summary>
    private static string LinkText(string text)
    {
        var clean = Whitespace.Replace(text ?? "", " ").Trim();
        if (clean.Length == 0)
            clean = "（沒有文字）";
        if (clean.Length > MaxTextLength)
            clean = clean[..MaxTextLength] + "…";
        return clean.Replace('[', '［').Replace(']', '］');
    }
}
