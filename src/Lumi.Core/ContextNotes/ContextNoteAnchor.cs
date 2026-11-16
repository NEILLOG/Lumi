using Lumi.Core.Prompts;

namespace Lumi.Core.ContextNotes;

/// <summary>附在使用者訊息後面給主 AI 看的「目標＋已決定」（計畫 4.8，<c>&lt;lumi_note_anchor&gt;</c>）。</summary>
public static class ContextNoteAnchor
{
    private const string Open = "<lumi_note_anchor>";
    private const string Close = "</lumi_note_anchor>";
    private const string EscapedClose = "<\\/lumi_note_anchor";
    private const string NothingDecided = "（還沒有）";

    /// <summary>目標＋已決定（不含整本研究本）。「已決定」的主文去掉原話，區塊內的換行換成空白。</summary>
    public static string Core(string goal, ContextNoteDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var decided = document.Section(ContextNoteDocument.Decided).Blocks
            .Select(b => ContextNoteQuote.Split(b.Text).Main.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' '))
            .ToList();
        var lines = decided.Count == 0 ? NothingDecided : string.Join("\n", decided);
        return $"<goal>\n{goal}\n</goal>\n<decided>\n{lines}\n</decided>";
    }

    /// <param name="core">由 <see cref="Core"/> 產生的內容。</param>
    /// <param name="fullNote">整本研究本；null 時沒有 <c>&lt;note&gt;</c> 區塊。</param>
    public static string Wrap(string core, string? fullNote)
    {
        ArgumentNullException.ThrowIfNull(core);
        var note = fullNote is null ? "" : $"<note>\n{Escape(fullNote)}\n</note>\n";
        return $"{Open}\n{PromptText.Get("context-note-anchor")}\n{Escape(core)}\n{note}{Close}";
    }

    /// <summary>從最近的往前找：第一個含錨的 suffix，取它裡面最後一個錨的 <c>&lt;goal&gt;</c> 到 <c>&lt;/decided&gt;</c>。沒有錨回 null。</summary>
    public static string? LastCoreIn(IEnumerable<string?> suffixes)
    {
        foreach (var suffix in suffixes.Reverse())
        {
            if (suffix is null || !suffix.Contains(Open, StringComparison.Ordinal))
                continue;
            return CoreOf(suffix);
        }
        return null;
    }

    /// <summary>有沒有任何一個（非 null）suffix 含錨。</summary>
    public static bool AnyIn(IEnumerable<string?> suffixes) =>
        suffixes.Any(s => s is not null && s.Contains(Open, StringComparison.Ordinal));

    private static string? CoreOf(string suffix)
    {
        var start = suffix.LastIndexOf(Open, StringComparison.Ordinal);
        var goal = suffix.IndexOf("<goal>", start, StringComparison.Ordinal);
        if (goal < 0)
            return null;
        // 跳脫過的內容裡不會再出現「</lumi_note_anchor>」，所以第一個就是真的結尾
        var close = suffix.IndexOf(Close, start, StringComparison.Ordinal);
        if (close < 0)
            return null;

        // 有整本研究本時，核心結束在它前面的「\n<note>\n」；沒有時結束在區塊結尾前最後一個 </decided>
        var note = suffix.IndexOf("\n<note>\n", goal, StringComparison.Ordinal);
        int end;
        if (note >= 0 && note < close)
            end = note;
        else
        {
            var decidedAt = suffix[goal..close].LastIndexOf("</decided>", StringComparison.Ordinal);
            if (decidedAt < 0)
                return null;
            end = goal + decidedAt + "</decided>".Length;
        }
        return suffix[goal..end].Replace("<\\/lumi_note_anchor", "</lumi_note_anchor", StringComparison.Ordinal);
    }

    private static string Escape(string text) => text.Replace("</lumi_note_anchor", EscapedClose, StringComparison.Ordinal);
}
