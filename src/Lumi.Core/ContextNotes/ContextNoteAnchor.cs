namespace Lumi.Core.ContextNotes;

/// <summary>附在使用者訊息後面給主 AI 看的「目標＋已決定」（計畫 4.8，<c>&lt;lumi_note_anchor&gt;</c>）。</summary>
public static class ContextNoteAnchor
{
    // TODO(35C)
    public static string Core(string goal, ContextNoteDocument document) => throw new NotImplementedException();

    // TODO(35C)
    public static string Wrap(string core, string? fullNote) => throw new NotImplementedException();

    // TODO(35C)
    public static string? LastCoreIn(IEnumerable<string?> suffixes) => throw new NotImplementedException();

    // TODO(35C)
    public static bool AnyIn(IEnumerable<string?> suffixes) => throw new NotImplementedException();
}
