namespace Lumi.Core.Notes;

/// <summary>一則筆記。<paramref name="Time"/> 與 <paramref name="Category"/> 都可能沒有（舊檔或手寫的內容）。</summary>
public sealed record NoteEntry(TimeOnly? Time, string? Category, string Content);

/// <summary>某一天的筆記（只有有記事的日期才會回傳）。</summary>
public sealed record NoteDay(DateOnly Date, IReadOnlyList<NoteEntry> Entries);
