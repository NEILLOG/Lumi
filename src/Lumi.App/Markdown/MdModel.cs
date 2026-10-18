namespace Lumi.App.Markdown;

/// <summary>行內元素。不依賴 Avalonia，方便單元測試。</summary>
public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdStrong(IReadOnlyList<MdInline> Children) : MdInline;

public sealed record MdEmphasis(IReadOnlyList<MdInline> Children) : MdInline;

public sealed record MdStrike(IReadOnlyList<MdInline> Children) : MdInline;

public sealed record MdCode(string Text) : MdInline;

/// <summary>連結。網址是否能點由 <see cref="MarkdownLinks.IsAllowed"/> 決定（白名單協定）。</summary>
public sealed record MdLink(string Url, IReadOnlyList<MdInline> Children) : MdInline;

/// <summary>換行。軟換行與硬換行一律顯示成換行（聊天訊息常靠單一換行排版）。</summary>
public sealed record MdLineBreak : MdInline;

/// <summary>
/// 區塊元素。<paramref name="Source"/> 是這個區塊在原文裡的文字，
/// 串流增量渲染時用它判斷「這個區塊有沒有變」。
/// </summary>
public abstract record MdBlock(string Source);

public sealed record MdParagraph(string Source, IReadOnlyList<MdInline> Inlines) : MdBlock(Source);

public sealed record MdHeading(string Source, int Level, IReadOnlyList<MdInline> Inlines) : MdBlock(Source);

/// <param name="Language">沒有語言時是 null。</param>
public sealed record MdCodeBlock(string Source, string? Language, string Code) : MdBlock(Source);

public sealed record MdQuote(string Source, IReadOnlyList<MdBlock> Blocks) : MdBlock(Source);

/// <param name="Start">有序清單的起始編號；無序清單沒有意義。</param>
public sealed record MdList(string Source, bool Ordered, int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock(Source);

public enum MdAlign { Left, Center, Right }

public sealed record MdTableCell(IReadOnlyList<MdInline> Inlines);

public sealed record MdTable(
    string Source,
    IReadOnlyList<MdTableCell> Header,
    IReadOnlyList<IReadOnlyList<MdTableCell>> Rows,
    IReadOnlyList<MdAlign> Alignments) : MdBlock(Source);

public sealed record MdRule(string Source) : MdBlock(Source);

public static class MarkdownLinks
{
    /// <summary>
    /// 只允許 http、https、mailto。其他（javascript:、file:、自訂協定…）顯示成純文字、不可點：
    /// 模型輸出的連結不能被拿來執行任何東西（agent.md 第 16 節的精神：不信任模型輸出）。
    /// </summary>
    public static bool IsAllowed(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
}
