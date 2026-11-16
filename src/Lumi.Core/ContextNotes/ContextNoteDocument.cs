using System.Text;
using System.Text.RegularExpressions;

namespace Lumi.Core.ContextNotes;

/// <summary>區段裡的一個區塊：一個最上層清單項目（含縮排的子行），或一段連續的非清單文字。<see cref="Text"/> 是各行 TrimEnd 後用 \n 接起來。</summary>
public sealed partial record NoteBlock(string Text)
{
    /// <summary>第一行符合 <c>^(- |\* |\d+\. )</c>。</summary>
    public bool IsListItem => ListItemStart().IsMatch(Text);

    [GeneratedRegex(@"^(- |\* |\d+\. )")]
    private static partial Regex ListItemStart();
}

public sealed record NoteSection(string Name, IReadOnlyList<NoteBlock> Blocks);

/// <summary>
/// 研究本的結構（計畫 4.4）：固定 7 個區段，每個區段是一串區塊。不可變；<see cref="WithSection"/> 回新的文件。
/// 兩份文件的每個區段區塊文字都相同時相等。
/// </summary>
public sealed class ContextNoteDocument : IEquatable<ContextNoteDocument>
{
    public const string Breakdown = "問題拆解", Decided = "已決定", Open = "待決問題", Views = "觀點與選項",
        Rejected = "否決的選項", Solution = "目前的解與待驗證", Side = "旁支";
    public static IReadOnlyList<string> SectionNames { get; } = [Breakdown, Decided, Open, Views, Rejected, Solution, Side];

    /// <summary>永遠 7 個，照 <see cref="SectionNames"/> 的順序。</summary>
    public IReadOnlyList<NoteSection> Sections { get; }

    public static ContextNoteDocument Empty { get; } = new([]);

    /// <summary>缺的區段補上空的、順序照 <see cref="SectionNames"/>。不認得的名稱或同一個區段出現兩次丟 <see cref="ArgumentException"/>。</summary>
    public ContextNoteDocument(IEnumerable<NoteSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var given = new Dictionary<string, NoteSection>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (!SectionNames.Contains(section.Name))
                throw new ArgumentException($"不認得的區段：{section.Name}", nameof(sections));
            if (!given.TryAdd(section.Name, section))
                throw new ArgumentException($"區段重複：{section.Name}", nameof(sections));
        }
        Sections = SectionNames
            .Select(name => given.TryGetValue(name, out var s) ? new NoteSection(name, s.Blocks.ToArray()) : new NoteSection(name, []))
            .ToArray();
    }

    /// <summary>寬鬆解析（計畫 4.4）：缺的區段補上空的；不認得的 <c>## X</c> 降成 <c>### X</c>；第一個標題之前的內容移到「問題拆解」最前面。</summary>
    public static ContextNoteDocument Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return Empty;

        // 第一段是第一個認得的標題之前的內容，先歸到「問題拆解」，排在最前面。
        var segments = new List<(string Name, List<string> Lines)> { (Breakdown, []) };
        var inFence = false;
        foreach (var line in NormalizeNewlines(markdown).Split('\n'))
        {
            if (!inFence)
            {
                var index = HeadingIndex(line);
                if (index >= 0)
                {
                    segments.Add((SectionNames[index], []));
                    continue;
                }
                segments[^1].Lines.Add(line.StartsWith("## ", StringComparison.Ordinal) ? "### " + line[3..] : line);
            }
            else
            {
                segments[^1].Lines.Add(line);
            }
            if (IsFenceLine(line))
                inFence = !inFence;
        }

        // 同一個區段出現兩次：第二次的區塊接在第一次後面（照出現順序加進去）。
        var blocks = SectionNames.ToDictionary(name => name, _ => new List<NoteBlock>(), StringComparer.Ordinal);
        foreach (var (name, lines) in segments)
            blocks[name].AddRange(SplitBlocks(lines));
        return new ContextNoteDocument(SectionNames.Select(name => new NoteSection(name, blocks[name])));
    }

    /// <summary>嚴格檢查（計畫 4.4，用在書記的輸出）。</summary>
    public static bool HasExactStructure(string markdown)
    {
        if (markdown is null)
            return false;

        var seen = 0;
        var inFence = false;
        foreach (var line in NormalizeNewlines(markdown).Split('\n'))
        {
            if (!inFence)
            {
                var index = HeadingIndex(line);
                if (index >= 0)
                {
                    // 順序錯、重複（index 小於 seen）都不過。
                    if (index != seen)
                        return false;
                    seen++;
                    continue;
                }
                if (line.StartsWith("## ", StringComparison.Ordinal))
                    return false;
            }
            if (seen == 0 && !string.IsNullOrWhiteSpace(line))
                return false;
            if (IsFenceLine(line))
                inFence = !inFence;
        }
        return seen == SectionNames.Count;
    }

    /// <summary>輸出（計畫 4.4）：<c>## 名稱</c>、空行、區塊；相鄰兩個都是清單區塊用一個換行，否則空一行；區段之間空一行；結尾一個換行。</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var section in Sections)
        {
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append("## ").Append(section.Name);
            // 標題後空一行；第一個區塊前也是空一行，之後的區塊看前後是不是都是清單。
            NoteBlock? previous = null;
            foreach (var block in section.Blocks)
            {
                sb.Append(previous is not null && previous.IsListItem && block.IsListItem ? "\n" : "\n\n");
                sb.Append(block.Text);
                previous = block;
            }
        }
        return sb.Append('\n').ToString();
    }

    /// <summary>標題行是 7 個認得的標題之一（整行完全相同）時回它的索引，否則 -1。</summary>
    private static int HeadingIndex(string line)
    {
        for (var i = 0; i < SectionNames.Count; i++)
            if (string.Equals(line, "## " + SectionNames[i], StringComparison.Ordinal))
                return i;
        return -1;
    }

    /// <summary>三個反引號開頭（不算同一行開合的 <c>```x```</c>）就是程式碼區塊的界線。</summary>
    private static bool IsFenceLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("```", StringComparison.Ordinal) && !(trimmed.Length >= 6 && trimmed.EndsWith("```", StringComparison.Ordinal));
    }

    private static bool IsListStart(string line)
    {
        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            return true;
        var digits = 0;
        while (digits < line.Length && char.IsDigit(line[digits]))
            digits++;
        return digits > 0 && line.Length >= digits + 2 && line[digits] == '.' && line[digits + 1] == ' ';
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>把一個區段的行切成區塊（計畫 4.4 的切法）。程式碼區塊內不切。</summary>
    private static List<NoteBlock> SplitBlocks(IReadOnlyList<string> lines)
    {
        var blocks = new List<NoteBlock>();
        List<string>? current = null;
        var currentIsList = false;
        // 清單區塊裡的空行先留著，遇到縮排行才併入；區塊結尾的空行不算。
        var pendingBlank = new List<string>();
        var inFence = false;

        void Finish()
        {
            if (current is not null)
                blocks.Add(new NoteBlock(string.Join('\n', current.Select(l => l.TrimEnd()))));
            current = null;
            pendingBlank.Clear();
        }

        foreach (var line in lines)
        {
            if (inFence)
            {
                current!.Add(line);
                if (IsFenceLine(line))
                    inFence = false;
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current is not null && currentIsList)
                    pendingBlank.Add(line);
                else
                    Finish();
                continue;
            }

            var indented = line[0] == ' ' || line[0] == '\t';
            var listStart = IsListStart(line);
            if (current is not null && currentIsList && indented)
            {
                current.AddRange(pendingBlank);
                pendingBlank.Clear();
                current.Add(line);
            }
            else if (current is not null && !currentIsList && !listStart)
            {
                current.Add(line);
            }
            else
            {
                Finish();
                current = [line];
                currentIsList = listStart;
            }
            if (IsFenceLine(line))
                inFence = true;
        }
        Finish();
        return blocks;
    }

    /// <summary>不認得的名稱丟 <see cref="ArgumentException"/>。</summary>
    public NoteSection Section(string name) =>
        Sections.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"不認得的區段：{name}", nameof(name));

    /// <summary>依文件順序列出全部區塊。</summary>
    public IEnumerable<(string Section, NoteBlock Block)> AllBlocks() =>
        Sections.SelectMany(s => s.Blocks.Select(b => (s.Name, b)));

    public ContextNoteDocument WithSection(string name, IReadOnlyList<NoteBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        _ = Section(name);
        return new ContextNoteDocument(Sections.Select(s => s.Name == name ? new NoteSection(name, blocks) : s));
    }

    public bool Equals(ContextNoteDocument? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        for (var i = 0; i < Sections.Count; i++)
        {
            var a = Sections[i].Blocks;
            var b = other.Sections[i].Blocks;
            if (a.Count != b.Count)
                return false;
            for (var j = 0; j < a.Count; j++)
                if (!string.Equals(a[j].Text, b[j].Text, StringComparison.Ordinal))
                    return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ContextNoteDocument);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var section in Sections)
        {
            hash.Add(section.Blocks.Count);
            foreach (var block in section.Blocks)
                hash.Add(block.Text, StringComparer.Ordinal);
        }
        return hash.ToHashCode();
    }
}

/// <summary>「已決定」的區塊第一行結尾附的使用者原話（D11：畫面預設收起）。</summary>
public static class ContextNoteQuote
{
    public const string Open = "（使用者原話：「", Close = "」）";

    /// <summary>
    /// 區塊第一行結尾是 <c>（使用者原話：「…」）</c> 時，回（去掉原話的主文, 原話）；否則回（原文, null）。
    /// 主文是第一行去掉原話後 TrimEnd，再接上其餘的行。原話裡可以再有「」。
    /// </summary>
    public static (string Main, string? Quote) Split(string blockText)
    {
        ArgumentNullException.ThrowIfNull(blockText);
        var newline = blockText.IndexOf('\n');
        var first = newline < 0 ? blockText : blockText[..newline];
        var rest = newline < 0 ? "" : blockText[newline..];
        var trimmed = first.TrimEnd();
        if (!trimmed.EndsWith(Close, StringComparison.Ordinal))
            return (blockText, null);
        var start = trimmed.LastIndexOf(Open, StringComparison.Ordinal);
        if (start < 0)
            return (blockText, null);
        var quoteStart = start + Open.Length;
        var quoteLength = trimmed.Length - Close.Length - quoteStart;
        if (quoteLength < 0)
            return (blockText, null);
        var quote = trimmed.Substring(quoteStart, quoteLength);
        var main = trimmed[..start].TrimEnd() + rest;
        return (main, quote);
    }
}
