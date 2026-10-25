namespace Lumi.Core.Browser;

public enum BrowserKind { Edge, Chrome }

public enum BrowserChoice { Auto, Edge, Chrome }

/// <summary>瀏覽器設定（M18）。設定頁的三項都是立即生效；視窗與瀏覽器的選擇在下次啟動瀏覽器時才套用。</summary>
public sealed record BrowserSettings
{
    public static readonly BrowserSettings Default = new();

    public BrowserChoice Choice { get; init; } = BrowserChoice.Auto;

    /// <summary>true＝顯示瀏覽器視窗（偵錯用）；false＝背景執行。</summary>
    public bool ShowWindow { get; init; }

    public BrowserSettings Normalize() => this with { Choice = Enum.IsDefined(Choice) ? Choice : BrowserChoice.Auto };
}

public static class BrowserLimits
{
    public const int ViewportWidth = 1280;
    public const int ViewportHeight = 800;
    public const int SnapshotDefaultChars = 12_000;
    public const int SnapshotMinChars = 2_000;
    public const int SnapshotMaxChars = 30_000;
    public const int MaxPagesPerConversation = 3;
    public const int MaxPagesTotal = 8;
    public const int WaitDefaultSeconds = 180;
    public const int WaitMinSeconds = 10;
    public const int WaitMaxSeconds = 600;
    public const int MaxScreenshotImages = 2;
    public const long MaxScreenshotBytes = 5L * 1024 * 1024;
    public const int FullPageMaxHeight = 4000;
    public static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(10);
}

public sealed record InstalledBrowser(BrowserKind Kind, string Path, string? Version)
{
    public string DisplayName => Kind == BrowserKind.Edge ? "Microsoft Edge" : "Google Chrome";
}

/// <param name="Selected">依設定選出要用的瀏覽器；沒有時是 null，<paramref name="Problem"/> 是給使用者看的說明。</param>
public sealed record BrowserAvailability(InstalledBrowser? Selected, IReadOnlyList<InstalledBrowser> Installed, string? Problem)
{
    public bool IsAvailable => Selected is not null;
}

/// <summary>偵測電腦上的 Edge 與 Chrome（實作在 Lumi.Browser 的 <c>BrowserLocator</c>）。</summary>
public interface IBrowserLocator
{
    /// <summary>依 Edge、Chrome 的順序列出找到的瀏覽器。</summary>
    IReadOnlyList<InstalledBrowser> Detect();

    /// <summary>Auto＝Edge 優先、其次 Chrome；Edge／Chrome＝只用指定的，沒有時 Selected 是 null 並說明。</summary>
    BrowserAvailability Resolve(BrowserChoice choice);
}

/// <param name="Url">頁面目前的網址。</param>
/// <param name="Text">AI 模式快照（已處理：連結絕對化、依長度上限截斷）。</param>
/// <param name="RefCount">快照裡有幾個 <c>[ref=…]</c>。</param>
public sealed record BrowserSnapshot(Uri Url, string? Title, string Text, int RefCount, bool Truncated);

/// <param name="Message">做了什麼（給模型看，繁中一句話；多個提示以換行接在後面，例如對話框、新分頁）。</param>
public sealed record BrowserOutcome(string Message, BrowserSnapshot Snapshot);

public sealed record BrowserShot(byte[] Data, string MediaType, int Width, int Height);

public sealed record BrowserTable(string? Caption, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <param name="Html">整頁（或元素）的 HTML，交給 <c>IWebContentExtractor</c> 轉 Markdown；<paramref name="Text"/> 是元素模式下的純文字。</param>
public sealed record BrowserPageContent(Uri Url, string? Title, string Html, string? Text);

public sealed record BrowserDownload(string FileName, long Bytes);

public enum BrowserScrollDirection { Up, Down, Top, Bottom }

/// <summary>至少要有一項；兩項都有時，任何一項成立就算完成。</summary>
public sealed record BrowserWaitCondition(string? UrlContains, string? TextVisible);

public sealed record BrowserWaitResult(bool Completed, string Message, BrowserSnapshot Snapshot);

public enum BrowserFailureKind { NotAvailable, LaunchFailed, ProxyNotSupported, NoPage, Closed, StaleRef, PasswordField, Navigation, Timeout, TooLarge, Other }

/// <summary><see cref="Exception.Message"/> 是給模型與使用者看的繁中句子，不含查詢字串與頁面內容。</summary>
public sealed class BrowserException(BrowserFailureKind kind, string message) : Exception(message)
{
    public BrowserFailureKind Kind { get; } = kind;
}

/// <summary>
/// 瀏覽器工作階段（實作在 Lumi.Browser 的 <c>PlaywrightBrowserSessions</c>）。每則對話一個分頁組，以 <c>conversationId</c> 區分。
/// 動作類方法做完都回新的快照（D4）。出錯一律丟 <see cref="BrowserException"/>；<paramref name="ct"/> 取消時丟 <see cref="OperationCanceledException"/>。
/// </summary>
public interface IBrowserSessions
{
    /// <summary>依目前設定偵測瀏覽器（同步；不啟動瀏覽器）。</summary>
    BrowserAvailability CheckAvailability();

    Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct);

    Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct);

    /// <param name="ref">null＝整頁；有值＝該元素。</param>
    Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct);

    Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct);

    Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct);

    Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct);

    Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct);

    Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct);

    /// <summary>密碼欄位丟 <see cref="BrowserFailureKind.PasswordField"/>。<paramref name="replace"/>＝先清空再輸入。</summary>
    Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct);

    Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct);

    Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct);

    /// <param name="fullPath">已由工具經 <c>IFileScope</c> 解析過的完整路徑。</param>
    Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct);

    /// <summary>點擊 <paramref name="ref"/> 並等它觸發的下載，存到 <paramref name="destinationPath"/>（呼叫端保證不存在、在範圍內、上層資料夾已存在）。</summary>
    Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct);

    Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct);

    /// <summary>目前分頁的網址；沒有分頁時 null（同步，給確認卡片用）。</summary>
    Uri? CurrentUrl(string conversationId);

    /// <summary>最近一次快照裡這個 ref 的描述，例如 <c>button "送出"</c>；沒有時 null（同步，給確認卡片用）。</summary>
    string? DescribeRef(string conversationId, string @ref);

    /// <summary>關閉這則對話的分頁；沒有時什麼都不做。</summary>
    Task CloseAsync(string conversationId, CancellationToken ct);

    /// <summary>關閉整個瀏覽器（Lumi 結束時）。</summary>
    Task CloseAllAsync();

    /// <summary>關閉瀏覽器並刪除 Lumi 專屬的瀏覽器設定檔（Edge 與 Chrome 兩份）。</summary>
    Task ClearProfileAsync(CancellationToken ct);
}

/// <summary>沒有瀏覽器支援時的預設（測試與尚未組裝時）：偵測回「沒有」，其他一律丟 <see cref="BrowserFailureKind.NotAvailable"/>。</summary>
public sealed class NullBrowserSessions : IBrowserSessions
{
    private const string Message = "這個版本沒有瀏覽器功能。";

    private static BrowserException Unavailable() => new(BrowserFailureKind.NotAvailable, Message);

    public BrowserAvailability CheckAvailability() => new(null, [], Message);

    public Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct) => throw Unavailable();

    public Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct) => throw Unavailable();

    public Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct) => throw Unavailable();

    public Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct) => throw Unavailable();

    public Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct) => throw Unavailable();

    public Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct) => throw Unavailable();

    public Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct) => throw Unavailable();

    public Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct) => throw Unavailable();

    public Uri? CurrentUrl(string conversationId) => null;

    public string? DescribeRef(string conversationId, string @ref) => null;

    public Task CloseAsync(string conversationId, CancellationToken ct) => Task.CompletedTask;

    public Task CloseAllAsync() => Task.CompletedTask;

    public Task ClearProfileAsync(CancellationToken ct) => Task.CompletedTask;
}
