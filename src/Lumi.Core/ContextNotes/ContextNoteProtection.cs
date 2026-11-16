using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumi.Core.ContextNotes;

/// <param name="Index">佔位符的編號（從 1 起）。</param>
/// <param name="Section">原本所在的區段。</param>
/// <param name="Text">原文。</param>
public sealed record ProtectedEntry(int Index, string Section, string Text);

/// <param name="Markdown">佔位符版的研究本（送給書記）。</param>
public sealed record MaskResult(string Markdown, IReadOnlyList<ProtectedEntry> Entries);

/// <param name="RestoredCount">被移動或刪除、程式放回去的受保護區塊數。</param>
public sealed record UnmaskResult(ContextNoteDocument Document, int RestoredCount);

/// <summary>受保護的內容（計畫 4.5，D10）：送給書記前換成佔位符，收回後換回原文。</summary>
public static class ContextNoteProtection
{
    public static string Placeholder(int index) => $"[[保護:{index}]]";

    /// <summary>完整的佔位符區塊（清單區塊去掉開頭的 <c>- </c> 後）。</summary>
    private static readonly Regex FullPlaceholderPattern = new(@"^(?:- )?\[\[保護:([0-9]+)\]\]$", RegexOptions.CultureInvariant);

    /// <summary>夾在文字裡的佔位符記號。</summary>
    private static readonly Regex MarkerPattern = new(@"\[\[保護:[0-9]+\]\]", RegexOptions.CultureInvariant);

    /// <summary>送給書記前（4.5）：依文件順序把受保護的區塊換成佔位符，清單區塊換成 <c>- [[保護:N]]</c>，段落區塊換成 <c>[[保護:N]]</c>。</summary>
    public static MaskResult Mask(ContextNoteDocument document, IReadOnlyCollection<string> protectedBlocks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(protectedBlocks);

        var protectedSet = new HashSet<string>(protectedBlocks, StringComparer.Ordinal);
        var entries = new List<ProtectedEntry>();
        var sections = new List<NoteSection>();
        foreach (var section in document.Sections)
        {
            var blocks = new List<NoteBlock>();
            foreach (var block in section.Blocks)
            {
                if (!protectedSet.Contains(block.Text))
                {
                    blocks.Add(block);
                    continue;
                }
                var index = entries.Count + 1;
                entries.Add(new ProtectedEntry(index, section.Name, block.Text));
                var placeholder = Placeholder(index);
                blocks.Add(new NoteBlock(block.IsListItem ? "- " + placeholder : placeholder));
            }
            sections.Add(new NoteSection(section.Name, blocks));
        }
        return new MaskResult(new ContextNoteDocument(sections).ToMarkdown(), entries);
    }

    /// <summary>收回後（4.5）：佔位符換回原文；被移動或刪除的原文放回原本的區段最後面。</summary>
    public static UnmaskResult Unmask(ContextNoteDocument scribeOutput, IReadOnlyList<ProtectedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(scribeOutput);
        ArgumentNullException.ThrowIfNull(entries);

        var byIndex = new Dictionary<int, ProtectedEntry>();
        foreach (var entry in entries)
            byIndex.TryAdd(entry.Index, entry);

        var restored = new HashSet<int>();
        var output = ContextNoteDocument.SectionNames.ToDictionary(name => name, _ => new List<NoteBlock>(), StringComparer.Ordinal);
        foreach (var section in scribeOutput.Sections)
        {
            foreach (var block in section.Blocks)
            {
                // 1 與 3：整塊就是佔位符。第一次出現的換回原文；重複或不認得的整塊丟掉。
                var full = FullPlaceholderPattern.Match(block.Text);
                if (full.Success)
                {
                    if (int.TryParse(full.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                        && byIndex.TryGetValue(index, out var entry) && restored.Add(index))
                        output[section.Name].Add(new NoteBlock(entry.Text));
                    continue;
                }

                // 2 與 3：佔位符夾在別的文字裡，把記號拿掉；拿掉後空了就丟掉整塊。
                if (!MarkerPattern.IsMatch(block.Text))
                {
                    output[section.Name].Add(block);
                    continue;
                }
                var stripped = MarkerPattern.Replace(block.Text, "").Trim();
                if (stripped is "" or "-")
                    continue;
                output[section.Name].Add(new NoteBlock(stripped));
            }
        }

        // 4：沒有以完整區塊出現的 N，原文放回原本區段的最後面（依 N 的順序）。
        var restoredCount = 0;
        foreach (var entry in byIndex.Values.OrderBy(e => e.Index))
        {
            if (restored.Contains(entry.Index))
                continue;
            var name = ContextNoteDocument.SectionNames.Contains(entry.Section, StringComparer.Ordinal) ? entry.Section : ContextNoteDocument.Breakdown;
            output[name].Add(new NoteBlock(entry.Text));
            restoredCount++;
        }

        var document = new ContextNoteDocument(ContextNoteDocument.SectionNames.Select(name => new NoteSection(name, output[name])));
        return new UnmaskResult(document, restoredCount);
    }

    /// <summary>使用者存檔後的保護清單（4.5）：上一版就受保護而且文字沒變，或上一版沒有這段文字（新增或改過），就受保護。依 <paramref name="edited"/> 的順序、不重複。</summary>
    public static IReadOnlyList<string> AfterUserEdit(ContextNoteDocument previous, IReadOnlyCollection<string> previousProtected, ContextNoteDocument edited)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(previousProtected);
        ArgumentNullException.ThrowIfNull(edited);

        var previousTexts = previous.AllBlocks().Select(x => x.Block.Text).ToHashSet(StringComparer.Ordinal);
        var protectedSet = new HashSet<string>(previousProtected, StringComparer.Ordinal);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, block) in edited.AllBlocks())
        {
            var text = block.Text;
            var wasProtectedAndUnchanged = protectedSet.Contains(text) && previousTexts.Contains(text);
            var isNewOrChanged = !previousTexts.Contains(text);
            if ((wasProtectedAndUnchanged || isNewOrChanged) && seen.Add(text))
                result.Add(text);
        }
        return result;
    }

    /// <summary>拿掉文件裡已經不存在的項目（存檔前呼叫）：只留文件裡還有一模一樣文字的，保留原順序、去重複。</summary>
    public static IReadOnlyList<string> Prune(ContextNoteDocument document, IReadOnlyCollection<string> protectedBlocks)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(protectedBlocks);

        var texts = document.AllBlocks().Select(x => x.Block.Text).ToHashSet(StringComparer.Ordinal);
        return protectedBlocks.Where(texts.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }
}
