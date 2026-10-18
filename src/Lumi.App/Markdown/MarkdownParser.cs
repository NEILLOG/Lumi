using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdigBlock = Markdig.Syntax.Block;

namespace Lumi.App.Markdown;

/// <summary>Markdown 文字 → 自己的區塊模型。HTML 一律當文字，不渲染。</summary>
public static class MarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .DisableHtml()
        .Build();

    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return [];

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        return ConvertBlocks(document, markdown);
    }

    private static List<MdBlock> ConvertBlocks(IEnumerable<MarkdigBlock> blocks, string text)
    {
        var result = new List<MdBlock>();
        foreach (var block in blocks)
        {
            if (Convert(block, text) is { } converted)
                result.Add(converted);
        }
        return result;
    }

    private static MdBlock? Convert(MarkdigBlock block, string text)
    {
        var source = Slice(block, text);
        switch (block)
        {
            case HeadingBlock heading:
                return new MdHeading(source, Math.Clamp(heading.Level, 1, 6), ConvertInlines(heading.Inline));

            case ParagraphBlock paragraph:
                return new MdParagraph(source, ConvertInlines(paragraph.Inline));

            case FencedCodeBlock fenced:
                return new MdCodeBlock(source, Language(fenced.Info), CodeText(fenced));

            case CodeBlock code:   // 縮排式程式碼區塊
                return new MdCodeBlock(source, null, CodeText(code));

            case QuoteBlock quote:
                return new MdQuote(source, ConvertBlocks(quote, text));

            case ListBlock list:
                var items = list.OfType<ListItemBlock>().Select(item => (IReadOnlyList<MdBlock>)ConvertBlocks(item, text)).ToList();
                var start = int.TryParse(list.OrderedStart, out var n) ? n : 1;
                return new MdList(source, list.IsOrdered, start, items);

            case ThematicBreakBlock:
                return new MdRule(source);

            case Table table:
                return ConvertTable(table, source);

            case HtmlBlock html:
                // HTML 不渲染：原樣當段落文字顯示
                return new MdParagraph(source, [new MdText(html.Lines.ToString())]);

            default:
                return null;   // 連結參照定義等，不顯示
        }
    }

    private static MdTable ConvertTable(Table table, string source)
    {
        var rows = table.OfType<TableRow>().ToList();
        var header = rows.FirstOrDefault(r => r.IsHeader);
        var body = rows.Where(r => !r.IsHeader).ToList();

        var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var alignments = Enumerable.Range(0, columnCount)
            .Select(i => i < table.ColumnDefinitions.Count ? table.ColumnDefinitions[i].Alignment : null)
            .Select(a => a switch
            {
                TableColumnAlign.Center => MdAlign.Center,
                TableColumnAlign.Right => MdAlign.Right,
                _ => MdAlign.Left,
            })
            .ToList();

        return new MdTable(source,
            header is null ? [] : ConvertRow(header),
            body.Select(ConvertRow).ToList(),
            alignments);
    }

    private static IReadOnlyList<MdTableCell> ConvertRow(TableRow row) =>
        row.OfType<TableCell>()
            .Select(cell => new MdTableCell(cell.OfType<ParagraphBlock>().SelectMany(p => ConvertInlines(p.Inline)).ToList()))
            .ToList();

    private static string? Language(string? info)
    {
        var language = info?.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(language) ? null : language;
    }

    private static string CodeText(LeafBlock code) => code.Lines.ToString().TrimEnd('\r', '\n');

    private static string Slice(MarkdigBlock block, string text)
    {
        var start = Math.Clamp(block.Span.Start, 0, text.Length);
        var length = Math.Clamp(block.Span.Length, 0, text.Length - start);
        return text.Substring(start, length);
    }

    // ---- 行內 ----

    private static List<MdInline> ConvertInlines(ContainerInline? container)
    {
        var result = new List<MdInline>();
        if (container is null)
            return result;

        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
            result.AddRange(ConvertInline(inline));
        return result;
    }

    private static IEnumerable<MdInline> ConvertInline(Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                yield return new MdText(literal.Content.ToString());
                break;

            case CodeInline code:
                yield return new MdCode(code.Content);
                break;

            case LineBreakInline:
                yield return new MdLineBreak();
                break;

            case HtmlEntityInline entity:
                yield return new MdText(entity.Transcoded.ToString());
                break;

            case HtmlInline html:
                yield return new MdText(html.Tag);
                break;

            case EmphasisInline emphasis:
                var children = ConvertInlines(emphasis);
                yield return emphasis.DelimiterChar == '~'
                    ? new MdStrike(children)
                    : emphasis.DelimiterCount >= 2 ? new MdStrong(children) : new MdEmphasis(children);
                break;

            case LinkInline { IsImage: true } image:
                // 圖片不渲染：顯示成「[圖片：說明]」的連結文字
                var alt = PlainText(image);
                yield return new MdLink(image.Url ?? "", [new MdText(alt.Length > 0 ? $"[圖片：{alt}]" : "[圖片]")]);
                break;

            case LinkInline link:
                var label = ConvertInlines(link);
                yield return new MdLink(link.Url ?? "", label.Count > 0 ? label : [new MdText(link.Url ?? "")]);
                break;

            case ContainerInline container:   // 其他容器：只取子內容
                foreach (var child in ConvertInlines(container))
                    yield return child;
                break;
        }
    }

    private static string PlainText(ContainerInline container)
    {
        var parts = new List<string>();
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: parts.Add(literal.Content.ToString()); break;
                case CodeInline code: parts.Add(code.Content); break;
                case ContainerInline child: parts.Add(PlainText(child)); break;
            }
        }
        return string.Concat(parts);
    }
}
