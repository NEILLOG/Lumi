using Lumi.Core.Browser;

namespace Lumi.Tools.Tests.Browser;

/// <summary>記錄呼叫、回傳預先設定的結果或丟出例外。</summary>
internal sealed class FakeBrowserSessions : IBrowserSessions
{
    public List<string> Calls { get; } = [];

    public Exception? Throw { get; set; }

    public BrowserAvailability Availability { get; set; } = new(null, [], "找不到 Microsoft Edge 或 Google Chrome，請先安裝 Microsoft Edge。");

    public Uri? Current { get; set; } = new("https://example.com/page");

    public Dictionary<string, string> Labels { get; } = [];

    public BrowserSnapshot Snapshot { get; set; } = new(new Uri("https://example.com/page"), "範例", "- button \"送出\" [ref=e3]", 1, false);

    public string Message { get; set; } = "完成。";

    public BrowserShot Shot { get; set; } = new([1, 2, 3], "image/jpeg", 1280, 800);

    public BrowserPageContent Content { get; set; } = new(new Uri("https://example.com/page"), "範例", "<html><body><p>內容</p></body></html>", "內容");

    public IReadOnlyList<BrowserTable> Tables { get; set; } = [];

    public BrowserDownload Download { get; set; } = new("a.pdf", 10);

    public BrowserWaitResult Wait { get; set; } = new(true, "使用者已完成。", new BrowserSnapshot(new Uri("https://example.com/page"), "範例", "", 0, false));

    private BrowserOutcome Outcome(string call)
    {
        Calls.Add(call);
        if (Throw is not null)
            throw Throw;
        return new BrowserOutcome(Message, Snapshot);
    }

    public BrowserAvailability CheckAvailability() => Availability;

    public Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct) => Task.FromResult(Outcome($"open {url.AbsoluteUri}"));

    public Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct)
    {
        Outcome($"snapshot {maxChars}");
        return Task.FromResult(Snapshot);
    }

    public Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct)
    {
        Outcome($"content {@ref}");
        return Task.FromResult(Content);
    }

    public Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct)
    {
        Outcome($"tables {@ref}");
        return Task.FromResult(Tables);
    }

    public Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct)
    {
        Outcome($"screenshot {fullPage}");
        return Task.FromResult(Shot);
    }

    public Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct) =>
        Task.FromResult(Outcome($"scroll {direction} {@ref}"));

    public Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct) => Task.FromResult(Outcome("back"));

    public Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct) => Task.FromResult(Outcome($"click {@ref}"));

    public Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct) =>
        Task.FromResult(Outcome($"type {@ref} {text} {replace} {submit}"));

    public Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct) =>
        Task.FromResult(Outcome($"select {@ref} {string.Join("|", values)}"));

    public Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct) => Task.FromResult(Outcome($"press {key}"));

    public Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct) =>
        Task.FromResult(Outcome($"upload {@ref} {fullPath}"));

    public Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct)
    {
        Outcome($"download {@ref} {destinationPath} {maxBytes}");
        return Task.FromResult(Download);
    }

    public Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct)
    {
        Outcome($"wait {condition.UrlContains}|{condition.TextVisible} {timeout.TotalSeconds}");
        return Task.FromResult(Wait);
    }

    public Uri? CurrentUrl(string conversationId) => Current;

    public string? DescribeRef(string conversationId, string @ref) => Labels.GetValueOrDefault(@ref);

    public Task CloseAsync(string conversationId, CancellationToken ct)
    {
        Calls.Add("close");
        return Task.CompletedTask;
    }

    public Task CloseAllAsync() => Task.CompletedTask;

    public Task ClearProfileAsync(CancellationToken ct)
    {
        Calls.Add("clear-profile");
        return Task.CompletedTask;
    }
}
