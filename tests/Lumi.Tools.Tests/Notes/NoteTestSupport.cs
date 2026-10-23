using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Notes;
using Lumi.Core.Tools;
using Lumi.Tools.Notes;

namespace Lumi.Tools.Tests.Notes;

/// <summary>可以改變的筆記資料夾（模擬使用者在設定頁改了資料夾）。</summary>
internal sealed class FolderBox(string folder) : INoteFolderProvider
{
    public string Folder { get; set; } = folder;
}

/// <summary>固定時間的假時鐘，本機時區可指定（預設 UTC）。</summary>
internal sealed class ClockAt(DateTimeOffset now, TimeZoneInfo? local = null) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => local ?? TimeZoneInfo.Utc;
}

/// <summary>每個測試一個暫存資料夾，用完整個刪掉。</summary>
internal sealed class NoteTempRoot : IDisposable
{
    public NoteTempRoot()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumi-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Dir(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}

/// <summary>讀檔的位元組解成文字（不去掉 BOM，所以 BOM 會被測試發現）。</summary>
internal static class NoteFiles
{
    public static string ReadRaw(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

    /// <summary>符號連結；這個環境不允許建立時回 false（測試用 Skip 略過）。</summary>
    public static bool TryLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>寫入永遠失敗的假筆記儲存（測試工具把 <see cref="NoteStoreException"/> 轉成給使用者的錯誤）。</summary>
internal sealed class FailingNoteStore(string message) : INoteStore
{
    public string FolderPath => "";

    public Task AppendAsync(DateOnly date, TimeOnly time, string? category, string content, CancellationToken ct) =>
        throw new NoteStoreException(message);

    public Task<IReadOnlyList<NoteDay>> ReadAsync(DateOnly from, DateOnly to, string? keyword, CancellationToken ct) =>
        throw new NoteStoreException(message);
}

/// <summary>對話儲存的測試替身：只實作搜尋（回傳預先設定的結果，並記下收到的參數）。</summary>
internal sealed class FakeSearchConversations : IConversationStore
{
    public List<ConversationSearchHit> Hits { get; } = [];

    public Exception? Failure { get; set; }

    public string? LastQuery { get; private set; }

    public int LastLimit { get; private set; }

    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        LastQuery = query;
        LastLimit = limit;
        if (Failure is not null)
            throw Failure;
        return Task.FromResult<IReadOnlyList<ConversationSearchHit>>(Hits.Take(limit).ToList());
    }

    public static Conversation Conv(string id, string title, DateTimeOffset updated) => new(id, title, updated.AddHours(-1), updated);

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotSupportedException();
}
