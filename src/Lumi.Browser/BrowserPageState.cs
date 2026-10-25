using System.Collections.Concurrent;
using Lumi.Core.Browser;
using Microsoft.Playwright;

namespace Lumi.Browser;

/// <summary>
/// 一則對話在瀏覽器裡的狀態（M18 18C）：這則對話開過的分頁、最近一次快照的 ref 描述、等著回報給模型的提示。
/// 同一則對話的操作用 <see cref="Gate"/> 排隊；分頁事件（對話框、新分頁、分頁被關）可能在 Playwright 的執行緒上觸發，
/// 所以分頁清單與目前分頁都在 <c>_sync</c> 鎖內讀寫。
/// </summary>
internal sealed class BrowserPageState
{
    private static readonly IReadOnlyDictionary<string, string> NoLabels = new Dictionary<string, string>();

    private readonly object _sync = new();
    private readonly List<IPage> _pages = [];
    private readonly TimeProvider _time;
    private IPage? _page;
    private IReadOnlyDictionary<string, string> _refLabels = NoLabels;
    private long _lastUsedTicks;

    public BrowserPageState(string conversationId, TimeProvider time)
    {
        ConversationId = conversationId;
        _time = time;
        _lastUsedTicks = time.GetUtcNow().UtcTicks;
    }

    public string ConversationId { get; }

    /// <summary>同一則對話的操作排隊用。等待使用者操作時不要持有它（見 18F）。</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>對話框、新分頁、下載的提示；下一次動作的結果取走（<see cref="TakeNotes"/>）。</summary>
    public ConcurrentQueue<string> Notes { get; } = new();

    /// <summary>目前分頁。這則對話沒有任何分頁時丟 <see cref="BrowserFailureKind.NoPage"/>。</summary>
    public IPage Page
    {
        get
        {
            lock (_sync)
                return _page ?? throw new BrowserException(BrowserFailureKind.NoPage, "這則對話的網頁分頁已經關閉，請重新用 browser_open 開啟。");
        }
    }

    /// <summary>目前分頁；沒有時 null（使用者關掉分頁或視窗）。</summary>
    public IPage? PageOrNull
    {
        get
        {
            lock (_sync)
                return _page;
        }
    }

    /// <summary>這則對話開過、還開著的分頁（目前分頁在最後）。每次取得都是複本。</summary>
    public IReadOnlyList<IPage> Pages
    {
        get
        {
            lock (_sync)
                return _pages.ToArray();
        }
    }

    /// <summary>最近一次快照的 ref → 描述表（原子替換）。</summary>
    public IReadOnlyDictionary<string, string> RefLabels
    {
        get => Volatile.Read(ref _refLabels);
        private set => Volatile.Write(ref _refLabels, value);
    }

    public DateTimeOffset LastUsed
    {
        get => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);
    }

    /// <summary>有操作正在進行（持有 <see cref="Gate"/>）。</summary>
    public bool IsBusy => Gate.CurrentCount == 0;

    public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, _time.GetUtcNow().UtcTicks);

    /// <summary>擷取目前分頁的 AI 模式快照（D4）並更新 <see cref="RefLabels"/>。</summary>
    public async Task<BrowserSnapshot> CaptureAsync(int maxChars, CancellationToken ct)
    {
        var page = Page;
        var raw = await page.AriaSnapshotAsync(new PageAriaSnapshotOptions { Mode = AriaSnapshotMode.Ai, Timeout = 15000 })
            .WaitAsync(ct).ConfigureAwait(false);
        var url = ToUri(page.Url);
        var parsed = BrowserSnapshotParser.Parse(raw, url, maxChars);
        RefLabels = parsed.RefLabels;
        var title = await page.TitleAsync().WaitAsync(ct).ConfigureAwait(false);
        return new BrowserSnapshot(url, string.IsNullOrWhiteSpace(title) ? null : title, parsed.Text, parsed.RefCount, parsed.Truncated);
    }

    /// <summary>等頁面穩定：DOMContentLoaded（最多 5 秒），再等網路閒置（最多 1 秒）。兩者逾時都不算錯。</summary>
    public async Task SettleAsync(CancellationToken ct)
    {
        var page = PageOrNull;
        if (page is null)
            return;
        await WaitStateAsync(page, LoadState.DOMContentLoaded, 5000, ct).ConfigureAwait(false);
        await WaitStateAsync(page, LoadState.NetworkIdle, 1000, ct).ConfigureAwait(false);
    }

    private static async Task WaitStateAsync(IPage page, LoadState state, float timeoutMs, CancellationToken ct)
    {
        try
        {
            await page.WaitForLoadStateAsync(state, new PageWaitForLoadStateOptions { Timeout = timeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // 逾時、頁面正在跳轉或剛被關閉：照現況繼續
        }
    }

    /// <summary>取走所有提示，以換行串接；沒有時空字串。</summary>
    public string TakeNotes()
    {
        List<string>? notes = null;
        while (Notes.TryDequeue(out var note))
            (notes ??= []).Add(note);
        return notes is null ? "" : string.Join('\n', notes);
    }

    /// <summary>加入新分頁並設為目前分頁（不論是不是彈出的）。超過上限時關閉最舊的。</summary>
    public void AddPage(IPage page)
    {
        Hook(page);
        IPage? evicted = null;
        lock (_sync)
        {
            _pages.Remove(page);
            _pages.Add(page);
            _page = page;
            if (_pages.Count > BrowserLimits.MaxPagesPerConversation)
                evicted = _pages[0];
        }
        if (evicted is not null)
            CloseQuietly(evicted);
    }

    /// <summary>新分頁（網站彈出的視窗，且它的來源是這則對話的分頁）：加入、變成目前分頁、加提示。</summary>
    public void Adopt(IPage popup)
    {
        AddPage(popup);
        var host = ToUri(popup.Url).Host;
        Notes.Enqueue($"點擊開啟了新分頁，目前已切換到新分頁：{(host.Length == 0 ? "空白頁" : host)}。");
    }

    /// <summary>瀏覽器為了顯示視窗重新啟動之後，原本的分頁都失效了：改用新的分頁。</summary>
    public void ResetForRestart(IPage page)
    {
        lock (_sync)
        {
            _pages.Clear();
            _page = null;
        }
        RefLabels = NoLabels;
        AddPage(page);
    }

    /// <summary>這則對話是不是擁有這個分頁（判斷彈出視窗的來源用）。</summary>
    public bool Owns(IPage page)
    {
        lock (_sync)
            return _pages.Contains(page);
    }

    /// <summary>訂閱分頁事件：對話框一律取消並記下（D11）；分頁被關掉時從清單移除。</summary>
    private void Hook(IPage page)
    {
        page.Dialog += async (_, dialog) =>
        {
            try
            {
                var message = dialog.Message ?? "";
                if (message.Length > 100)
                    message = message[..100] + "…";
                Notes.Enqueue($"頁面跳出對話框「{message}」，已取消。");
                await dialog.DismissAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // 對話框已經被關掉或分頁已關閉
            }
        };
        page.Close += (_, closed) =>
        {
            lock (_sync)
            {
                _pages.Remove(closed);
                if (ReferenceEquals(_page, closed))
                    _page = _pages.Count > 0 ? _pages[^1] : null;
            }
        };
    }

    internal static void CloseQuietly(IPage page)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 已經關閉或瀏覽器已結束
            }
        });
    }

    /// <summary><c>about:blank</c> 這類不是 http／https 的網址一律當 <c>about:blank</c>。</summary>
    internal static Uri ToUri(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : new Uri("about:blank");
}
