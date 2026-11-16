using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>版本清單的一列（不含全文）。</summary>
public sealed record ContextNoteRevisionRow(int Version, ContextNoteRevisionKind Kind, string VersionText, string AuthorText, string TimeText, string ReasonText)
{
    /// <summary>第二行的說明：時間，有原因時接在後面。</summary>
    public string CaptionText => ReasonText.Length == 0 ? TimeText : $"{TimeText} · {ReasonText}";
}

/// <summary>差異區塊的種類（只有這三種會出現在畫面上；沒變的區塊不列）。</summary>
public enum ContextNoteDiffRowKind { Added, Removed, Changed }

/// <summary>差異的一列。Changed 時 <see cref="Segments"/> 有值，<see cref="Text"/> 是新的文字。</summary>
public sealed record ContextNoteDiffRow(ContextNoteDiffRowKind Kind, string KindText, string Text, IReadOnlyList<DiffSegment>? Segments)
{
    public bool IsAdded => Kind == ContextNoteDiffRowKind.Added;
    public bool IsRemoved => Kind == ContextNoteDiffRowKind.Removed;
    public bool IsChanged => Kind == ContextNoteDiffRowKind.Changed;
}

/// <summary>差異的一個區段（只有有變的才會建立）。</summary>
public sealed record ContextNoteDiffSection(string Name, IReadOnlyList<ContextNoteDiffRow> Rows);
