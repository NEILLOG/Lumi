using System.Text;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Lumi.App.Markdown;

/// <summary>
/// 複製 Markdown：剪貼簿同時放 Markdown 原文（純文字，記事本、VS Code 拿到原文）與轉好的 HTML
/// （Word、Outlook、Teams 貼上時保留標題、粗體、清單、表格）。
/// HTML 的剪貼簿格式各平台不同：Windows 是 CF_HTML（<c>HTML Format</c>，UTF-8 加位移標頭），macOS 是 <c>public.html</c>；其他平台只放純文字。
/// </summary>
public static class MarkdownClipboard
{
    private static readonly DataFormat<byte[]> WindowsHtml = DataFormat.CreateBytesPlatformFormat("HTML Format");
    private static readonly DataFormat<string> MacHtml = DataFormat.CreateStringPlatformFormat("public.html");

    // 和畫面上的渲染（MarkdownParser）同一套語法；原文裡的 HTML 一律當文字
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseCjkFriendlyEmphasis()   // 「**重點：**內容」的粗體結尾緊接標點、後面接中文時也算結束（FX-12）
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static Task SetAsync(IClipboard clipboard, string markdown) => SetAsync(clipboard, markdown, plainText: markdown);

    /// <summary>
    /// 表格的複製鈕：HTML 用表格的 Markdown（<paramref name="markdown"/>），純文字用 Tab 分隔（<paramref name="tabText"/>，
    /// 貼到 Excel 會自動分欄，貼到記事本也看得懂）。
    /// </summary>
    public static Task SetTableAsync(IClipboard clipboard, string markdown, string tabText) => SetAsync(clipboard, markdown, plainText: tabText);

    private static async Task SetAsync(IClipboard clipboard, string markdown, string plainText)
    {
        var item = new DataTransferItem();
        item.SetText(plainText);
        if (OperatingSystem.IsWindows())
            item.Set(WindowsHtml, ToCfHtml(ToHtml(markdown)));
        else if (OperatingSystem.IsMacOS())
            item.Set(MacHtml, ToHtmlDocument(ToHtml(markdown)));

        var transfer = new DataTransfer();
        transfer.Add(item);
        await clipboard.SetDataAsync(transfer);
    }

    /// <summary>
    /// 表格轉 Tab 分隔的純文字：每列一行，儲存格之間用 Tab，空儲存格保留成空欄，欄數和表頭（最寬的一列）一致。
    /// 只留文字（粗體、連結等格式不保留，連結只留文字）；儲存格裡的換行與 Tab 改成空白。Markdown 的跳脫（<c>\|</c>）已經變成文字，不再加回去。
    /// </summary>
    public static string ToTabText(MdTable table)
    {
        var rows = new List<IReadOnlyList<MdTableCell>>();
        if (table.Header.Count > 0)
            rows.Add(table.Header);
        rows.AddRange(table.Rows);

        var columns = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        return string.Join("\n", rows.Select(row => string.Join("\t", Enumerable.Range(0, columns).Select(c =>
            c < row.Count ? CellText(row[c].Inlines) : ""))));
    }

    private static string CellText(IEnumerable<MdInline> inlines)
    {
        var builder = new StringBuilder();
        AppendPlain(builder, inlines);
        return builder.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').ToString();
    }

    private static void AppendPlain(StringBuilder builder, IEnumerable<MdInline> inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText t: builder.Append(t.Text); break;
                case MdCode c: builder.Append(c.Text); break;
                case MdStrong s: AppendPlain(builder, s.Children); break;
                case MdEmphasis e: AppendPlain(builder, e.Children); break;
                case MdStrike s: AppendPlain(builder, s.Children); break;
                case MdLink l: AppendPlain(builder, l.Children); break;
                case MdLineBreak: builder.Append(' '); break;
            }
        }
    }

    /// <summary>
    /// Markdown → HTML 片段。和畫面一致：只有 http、https、mailto 的連結保留成連結，其他協定只留文字；
    /// 圖片不放 <c>&lt;img&gt;</c>（貼上時不去抓外部圖片），換成「[圖片：說明]」文字。表格加上框線（Word 預設沒有）。
    /// </summary>
    public static string ToHtml(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage)
            {
                var alt = PlainText(link);
                link.ReplaceBy(new LiteralInline(alt.Length > 0 ? $"[圖片：{alt}]" : "[圖片]"), copyChildren: false);
            }
            else if (!MarkdownLinks.IsAllowed(link.Url ?? ""))
            {
                link.ReplaceBy(new LiteralInline(PlainText(link)), copyChildren: false);
            }
        }

        var html = document.ToHtml(Pipeline);
        return html.Replace("<table>", "<table border=\"1\" cellpadding=\"4\" style=\"border-collapse:collapse\">", StringComparison.Ordinal);
    }

    private static string ToHtmlDocument(string fragment) =>
        "<html><head><meta charset=\"utf-8\"></head><body>" + fragment + "</body></html>";

    /// <summary>
    /// Windows 的 CF_HTML：標頭記下 HTML 與片段在 UTF-8 位元組裡的起訖位置（10 位數補零，長度固定，先算好再填）。
    /// </summary>
    public static byte[] ToCfHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";

        var headerLength = Encoding.UTF8.GetByteCount(string.Format(header, 0, 0, 0, 0));
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);

        return Encoding.UTF8.GetBytes(string.Format(header, startHtml, endHtml, startFragment, endFragment) + before + fragment + after);
    }

    private static string PlainText(ContainerInline container)
    {
        var builder = new StringBuilder();
        foreach (var inline in container.Descendants<LeafInline>())
        {
            switch (inline)
            {
                case LiteralInline literal: builder.Append(literal.Content.ToString()); break;
                case CodeInline code: builder.Append(code.Content); break;
            }
        }
        return builder.ToString();
    }
}
