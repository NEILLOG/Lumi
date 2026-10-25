using Lumi.Core.Browser;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// App 測試用的瀏覽器工作階段：只有設定頁用得到的 <see cref="ClearProfileAsync"/> 與 <see cref="CheckAvailability"/> 有行為，
/// 其他方法（導覽、點擊……）設定頁不會呼叫，丟 <see cref="NotImplementedException"/>。
/// </summary>
internal sealed class FakeBrowserSessions : IBrowserSessions
{
    /// <summary><see cref="ClearProfileAsync"/> 要丟的例外（沒有就成功）。</summary>
    public BrowserException? ClearFailure { get; set; }

    /// <summary>被呼叫過的次數（確認有沒有真的清除）。</summary>
    public int ClearCalls { get; private set; }

    /// <summary>有設定時，<see cref="ClearProfileAsync"/> 要等它完成（測試「執行中」用）。</summary>
    public TaskCompletionSource? ClearGate { get; set; }

    public async Task ClearProfileAsync(CancellationToken ct)
    {
        ClearCalls++;
        if (ClearGate is not null)
            await ClearGate.Task;
        if (ClearFailure is not null)
            throw ClearFailure;
    }

    public BrowserAvailability CheckAvailability() => throw new NotImplementedException();

    public Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct) =>
        throw new NotImplementedException();

    public Uri? CurrentUrl(string conversationId) => throw new NotImplementedException();

    public string? DescribeRef(string conversationId, string @ref) => throw new NotImplementedException();

    public Task CloseAsync(string conversationId, CancellationToken ct) => throw new NotImplementedException();

    public Task CloseAllAsync() => throw new NotImplementedException();
}
