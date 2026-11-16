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

    // TODO(35B)
    /// <summary>寬鬆解析（計畫 4.4）：缺的區段補上空的；不認得的 <c>## X</c> 降成 <c>### X</c>；第一個標題之前的內容移到「問題拆解」最前面。</summary>
    public static ContextNoteDocument Parse(string markdown) => throw new NotImplementedException();

    // TODO(35B)
    /// <summary>嚴格檢查（計畫 4.4，用在書記的輸出）。</summary>
    public static bool HasExactStructure(string markdown) => throw new NotImplementedException();

    // TODO(35B)
    public string ToMarkdown() => throw new NotImplementedException();

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
