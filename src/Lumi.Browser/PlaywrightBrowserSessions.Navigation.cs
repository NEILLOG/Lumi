using Lumi.Core.Browser;

namespace Lumi.Browser;

// 導覽與讀取（18D）
public sealed partial class PlaywrightBrowserSessions
{
    public Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct) => throw new NotImplementedException();
}
