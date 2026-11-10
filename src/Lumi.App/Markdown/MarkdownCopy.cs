using System.Text;
using Avalonia;
using Avalonia.Controls;

namespace Lumi.App.Markdown;

/// <summary>
/// 一段文字的行內格式（以 <c>TextLayout</c> 的文字位置計，換行占 1）。<paramref name="Link"/> 是可點的網址；不可點的連結當一般文字。
/// </summary>
public sealed record MarkdownSpan(int Start, string Text, bool Bold, bool Italic, bool Strike, bool Code, string? Link)
{
    public int End => Start + Text.Length;
}

public enum MarkdownCopyKind { Text, Code, TableCell }

/// <summary>
/// 一個可選取文字區塊在原文裡的位置。<paramref name="Root"/> 是它所屬的最上層區塊（同一個清單、引用、表格的區塊相同，比對用參考相等）；
/// <paramref name="Prefix"/> 加在第一行前面（「## 」「- 」「&gt; 」），<paramref name="Indent"/> 加在之後每一行前面。
/// </summary>
public sealed record MarkdownCopyInfo(object Root, string Prefix, string Indent, MarkdownCopyKind Kind, IReadOnlyList<MarkdownSpan> Spans,
    string? Language = null, int Row = 0, bool HeaderRow = false);

/// <summary>一個文字區塊裡選取的範圍；<paramref name="SelectedText"/> 是畫面上的文字（程式碼區塊直接用它）。</summary>
public sealed record MarkdownSelectionPart(MarkdownCopyInfo Info, int Start, int End, string SelectedText);

/// <summary>
/// 選取的文字複製成 Markdown：渲染時在每個文字區塊記下行內格式與所在的區塊（<see cref="InfoProperty"/>），
/// 複製時依選取範圍組回 Markdown。只選到一個區塊時只保留行內格式（粗體、程式碼、連結），不加標題、清單符號；
/// 選到多個區塊時連同標題、清單、引用、程式碼區塊、表格一起還原。
/// </summary>
public static class MarkdownCopy
{
    public static readonly AttachedProperty<MarkdownCopyInfo?> InfoProperty =
        AvaloniaProperty.RegisterAttached<Control, MarkdownCopyInfo?>("Info", typeof(MarkdownCopy));

    public static MarkdownCopyInfo? GetInfo(Control control) => control.GetValue(InfoProperty);

    public static void SetInfo(Control control, MarkdownCopyInfo? value) => control.SetValue(InfoProperty, value);

    public static string Write(IReadOnlyList<MarkdownSelectionPart> parts)
    {
        if (parts.Count == 0)
            return "";
        if (parts.Count == 1)
        {
            var only = parts[0];
            return only.Info.Kind == MarkdownCopyKind.Code ? only.SelectedText : Inline(only.Info.Spans, only.Start, only.End);
        }

        var builder = new StringBuilder();
        MarkdownCopyInfo? previous = null;
        for (var i = 0; i < parts.Count;)
        {
            var info = parts[i].Info;
            string text;
            if (info.Kind == MarkdownCopyKind.TableCell)
            {
                var end = i;
                while (end < parts.Count && parts[end].Info.Kind == MarkdownCopyKind.TableCell && ReferenceEquals(parts[end].Info.Root, info.Root))
                    end++;
                text = Table(parts.Skip(i).Take(end - i).ToList());
                i = end;
            }
            else
            {
                text = info.Kind == MarkdownCopyKind.Code ? CodeBlock(parts[i]) : Lines(info, Inline(info.Spans, parts[i].Start, parts[i].End));
                i++;
            }

            if (previous is not null)
                builder.Append(Separator(previous, info));
            builder.Append(text);
            previous = info;
        }
        return builder.ToString();
    }

    /// <summary>區塊之間：同一個清單換一行；同一個引用空一行但保留「&gt;」；其他空一行。</summary>
    private static string Separator(MarkdownCopyInfo previous, MarkdownCopyInfo next)
    {
        if (!ReferenceEquals(previous.Root, next.Root))
            return "\n\n";
        return previous.Root is MdList ? "\n" : "\n" + next.Indent.TrimEnd() + "\n";
    }

    private static string Lines(MarkdownCopyInfo info, string content)
    {
        var lines = content.Split('\n');
        var builder = new StringBuilder(info.Prefix).Append(lines[0]);
        foreach (var line in lines.Skip(1))
            builder.Append('\n').Append(info.Indent).Append(line);
        return builder.ToString();
    }

    private static string CodeBlock(MarkdownSelectionPart part)
    {
        var code = part.SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var fence = new string('`', Math.Max(3, LongestRun(code, '`') + 1));
        var builder = new StringBuilder(part.Info.Prefix).Append(fence).Append(part.Info.Language);
        foreach (var line in code.Split('\n'))
            builder.Append('\n').Append(part.Info.Indent).Append(line);
        return builder.Append('\n').Append(part.Info.Indent).Append(fence).ToString();
    }

    /// <summary>表格：選到的儲存格逐列接成「| a | b |」，標題列後面補分隔列。</summary>
    private static string Table(IReadOnlyList<MarkdownSelectionPart> cells)
    {
        var builder = new StringBuilder();
        foreach (var row in cells.GroupBy(c => c.Info.Row))
        {
            var info = row.First().Info;
            builder.Append(builder.Length == 0 ? info.Prefix : "\n" + info.Indent);
            var texts = row.Select(c => Inline(c.Info.Spans, c.Start, c.End).Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)).ToList();
            builder.Append("| ").Append(string.Join(" | ", texts)).Append(" |");
            if (info.HeaderRow)
                builder.Append('\n').Append(info.Indent).Append('|').Append(string.Concat(texts.Select(_ => " --- |")));
        }
        return builder.ToString();
    }

    /// <summary>一個文字區塊裡 <paramref name="start"/>～<paramref name="end"/> 的文字，加上行內格式的符號。</summary>
    public static string Inline(IReadOnlyList<MarkdownSpan> spans, int start, int end)
    {
        // 切出選取範圍內的片段，格式相同的相鄰片段合併（emoji 會把同一段文字切成好幾個 Run）
        var pieces = new List<MarkdownSpan>();
        foreach (var span in spans)
        {
            var from = Math.Max(span.Start, start);
            var to = Math.Min(span.End, end);
            if (from >= to)
                continue;
            var text = span.Text.Substring(from - span.Start, to - from);
            if (pieces.Count > 0 && SameFormat(pieces[^1], span))
                pieces[^1] = pieces[^1] with { Text = pieces[^1].Text + text };
            else
                pieces.Add(span with { Start = from, Text = text });
        }

        var builder = new StringBuilder();
        for (var i = 0; i < pieces.Count;)
        {
            if (pieces[i].Link is not { } url)
            {
                builder.Append(Format(pieces[i]));
                i++;
                continue;
            }

            // 連結：同一個網址的相鄰片段放進同一組 [ ](url)
            var label = new StringBuilder();
            while (i < pieces.Count && pieces[i].Link == url)
                label.Append(Format(pieces[i++]));
            builder.Append('[').Append(label).Append("](").Append(url).Append(')');
        }
        return builder.ToString();
    }

    private static bool SameFormat(MarkdownSpan a, MarkdownSpan b) =>
        a.Bold == b.Bold && a.Italic == b.Italic && a.Strike == b.Strike && a.Code == b.Code && a.Link == b.Link;

    private static string Format(MarkdownSpan piece)
    {
        var text = piece.Text;
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string lead = "", trail = "", core;
        if (piece.Code)
        {
            var ticks = new string('`', LongestRun(text, '`') + 1);
            var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
            core = ticks + pad + text + pad + ticks;
        }
        else
        {
            // 符號要貼著文字（「** 粗體**」不算粗體），前後的空白與換行移到符號外面
            core = text.Trim();
            lead = text[..(text.Length - text.TrimStart().Length)];
            trail = text[(lead.Length + core.Length)..];
        }

        if (piece.Strike) core = "~~" + core + "~~";
        if (piece.Italic) core = "*" + core + "*";
        if (piece.Bold) core = "**" + core + "**";
        return lead + core + trail;
    }

    private static int LongestRun(string text, char c)
    {
        int longest = 0, current = 0;
        foreach (var ch in text)
        {
            current = ch == c ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }
}
