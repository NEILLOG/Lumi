using System.Collections.Concurrent;
using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Lumi.Browser;

/// <summary>
/// 瀏覽器程序的管理員（M18 18C）：一個 Playwright 持久設定檔的瀏覽器、每則對話一份 <see cref="BrowserPageState"/>。
/// 啟動、關閉、重啟用 <c>_lifecycle</c> 排隊；閒置 10 分鐘（D10）、使用者關掉視窗、Lumi 結束都會關閉整個瀏覽器。
/// log 只寫瀏覽器種類、版本、是否可見與原因，不寫網址、頁面內容或 Proxy 密碼。
/// </summary>
internal sealed class BrowserHost : IAsyncDisposable
{
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    private readonly IBrowserLocator _locator;
    private readonly IModelCatalog _catalog;
    private readonly ISecretStore _secrets;
    private readonly BrowserOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<InstalledBrowser, BrowserSettings, NetworkSettings, string?, string, bool, BrowserLaunchSpec> _planner;

    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ConcurrentDictionary<string, BrowserPageState> _states = new(StringComparer.Ordinal);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private EventHandler<IBrowserContext>? _closeHandler;
    private EventHandler<IPage>? _pageHandler;
    private IPage? _spare;
    private ITimer? _idleTimer;
    private bool _visible;
    private long _lastActivityTicks;
    private bool _disposed;

    public BrowserHost(
        IBrowserLocator locator, IModelCatalog catalog, ISecretStore secrets, BrowserOptions options, TimeProvider time, ILogger logger)
        : this(locator, catalog, secrets, options, time, logger, null)
    {
    }

    /// <param name="planner">測試用：換掉 <see cref="BrowserLaunchPlanner.Plan"/>。</param>
    internal BrowserHost(
        IBrowserLocator locator, IModelCatalog catalog, ISecretStore secrets, BrowserOptions options, TimeProvider time, ILogger logger,
        Func<InstalledBrowser, BrowserSettings, NetworkSettings, string?, string, bool, BrowserLaunchSpec>? planner)
    {
        _locator = locator;
        _catalog = catalog;
        _secrets = secrets;
        _options = options;
        _time = time;
        _logger = logger;
        _planner = planner ?? BrowserLaunchPlanner.Plan;
        _lastActivityTicks = time.GetUtcNow().UtcTicks;
    }

    /// <summary>瀏覽器程序目前是不是開著（使用者關掉視窗、閒置關閉、CloseAll 之後是 false）。</summary>
    public bool IsRunning => Volatile.Read(ref _context) is not null;

    /// <summary>目前是不是顯示視窗的模式（<see cref="EnsureVisibleAsync"/> 之後維持到瀏覽器關閉）。</summary>
    public bool IsVisible => _visible;

    /// <summary>同步查某則對話的狀態（給確認卡片用）；沒有時 null。</summary>
    public BrowserPageState? TryGetState(string conversationId) =>
        _states.TryGetValue(conversationId, out var state) ? state : null;

    /// <summary>
    /// 取得這則對話的狀態並更新 <see cref="BrowserPageState.LastUsed"/>。<paramref name="create"/>＝true 時必要的話啟動瀏覽器並開分頁。
    /// </summary>
    public async Task<BrowserPageState> GetStateAsync(string conversationId, bool create, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!create)
            {
                if (_context is null)
                    throw new BrowserException(BrowserFailureKind.Closed, "瀏覽器已關閉（閒置太久），請重新用 browser_open 開啟。");
                if (!_states.TryGetValue(conversationId, out var existing))
                    throw new BrowserException(BrowserFailureKind.NoPage, "這則對話還沒有開啟網頁，請先用 browser_open。");
                existing.Touch();
                return existing;
            }

            await EnsureStartedLockedAsync(_visible, ct).ConfigureAwait(false);
            var state = _states.GetOrAdd(conversationId, id => new BrowserPageState(id, _time));
            if (state.PageOrNull is null)
            {
                var page = await NewPageLockedAsync(ct).ConfigureAwait(false);
                state.AddPage(page);
            }
            state.Touch();
            await EvictOverLimitLockedAsync(conversationId).ConfigureAwait(false);
            return state;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// 確保瀏覽器是顯示視窗的模式（D9）。已經可見就直接回；瀏覽器沒有在執行時什麼都不做（之後取狀態會丟 <c>Closed</c>）。
    /// 否則關閉並以同一個設定檔重新啟動成可見，各對話的目前分頁重新開啟原網址，並留下提示。
    /// </summary>
    public async Task EnsureVisibleAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_context is null || _visible)
                return;

            var urls = new List<(BrowserPageState State, string? Url)>();
            foreach (var state in _states.Values)
            {
                var url = state.PageOrNull?.Url;
                urls.Add((state, Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? url : null));
            }

            _logger.LogInformation("瀏覽器重新啟動成顯示視窗的模式。");
            await CloseContextLockedAsync("改成顯示視窗", keepStates: true).ConfigureAwait(false);
            await EnsureStartedLockedAsync(true, ct).ConfigureAwait(false);

            var timeout = Math.Clamp(_catalog.Current.Network.TimeoutSeconds, 5, 60) * 1000f;
            foreach (var (state, url) in urls)
            {
                if (state.PageOrNull is null && url is null)
                    continue;
                var page = await NewPageLockedAsync(ct).ConfigureAwait(false);
                state.ResetForRestart(page);
                if (url is not null)
                {
                    try
                    {
                        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeout })
                            .WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (PlaywrightException)
                    {
                        // 重新載入失敗不影響重啟；模型會從快照看到目前的樣子
                    }
                }
                state.Notes.Enqueue("瀏覽器為了顯示視窗重新啟動過，頁面已重新載入，請先 browser_snapshot 再操作。");
                state.Touch();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>關閉這則對話的所有分頁並移除狀態；沒有任何狀態了就開始閒置倒數（不立即關瀏覽器）。</summary>
    public async Task CloseConversationAsync(string conversationId, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_states.TryRemove(conversationId, out var state))
                return;
            await ClosePagesLockedAsync(state).ConfigureAwait(false);
            if (_states.IsEmpty)
                Volatile.Write(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>關閉整個瀏覽器程序（所有分頁、Playwright 驅動）。</summary>
    public async Task CloseAllAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseContextLockedAsync("關閉").ConfigureAwait(false);
            DisposePlaywright();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>關閉瀏覽器並刪除 Lumi 專屬的設定檔（Edge 與 Chrome 兩份）。</summary>
    public async Task ClearProfileAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseContextLockedAsync("清除登入狀態").ConfigureAwait(false);
            DisposePlaywright();
            foreach (var name in new[] { "edge", "chrome" })
                await DeleteDirectoryAsync(Path.Combine(_options.ProfileRoot, name), ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>閒置檢查（D10）：所有對話都超過 <see cref="BrowserLimits.IdleClose"/> 沒有用、而且沒有進行中的操作時，關閉瀏覽器。</summary>
    internal async Task CheckIdleAsync()
    {
        if (_context is null || !_lifecycle.Wait(0))
            return;
        try
        {
            if (_context is null || !IsIdle())
                return;
            _logger.LogInformation("瀏覽器閒置超過 {Minutes} 分鐘，自動關閉。", (int)BrowserLimits.IdleClose.TotalMinutes);
            await CloseContextLockedAsync("閒置").ConfigureAwait(false);
            DisposePlaywright();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        await CloseAllAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private bool IsIdle()
    {
        var latest = new DateTimeOffset(Volatile.Read(ref _lastActivityTicks), TimeSpan.Zero);
        foreach (var state in _states.Values)
        {
            if (state.IsBusy)
                return false;
            if (state.LastUsed > latest)
                latest = state.LastUsed;
        }
        return _time.GetUtcNow() - latest >= BrowserLimits.IdleClose;
    }

    // ---- 以下都在 _lifecycle 內呼叫 ----

    private async Task EnsureStartedLockedAsync(bool visible, CancellationToken ct)
    {
        if (_context is not null)
            return;

        var settings = _catalog.Current;
        var availability = _locator.Resolve(settings.Browser.Choice);
        if (availability.Selected is not { } browser)
            throw new BrowserException(BrowserFailureKind.NotAvailable,
                availability.Problem ?? "找不到 Microsoft Edge 或 Google Chrome，請先安裝 Microsoft Edge。");

        string? proxyPassword = null;
        if (settings.Network.Auth == ProxyAuthMode.Account)
        {
            try
            {
                proxyPassword = (await _secrets.GetAsync(NetworkSecrets.ProxyPassword, ct).ConfigureAwait(false))?.Reveal();
            }
            catch (SecretStoreException)
            {
                proxyPassword = null; // 讀不到當作沒有密碼
            }
        }

        // PAC 是 file:// 等不支援的組合在這裡丟 BrowserException(ProxyNotSupported)，原樣往上傳
        var spec = _planner(browser, settings.Browser, settings.Network, proxyPassword, _options.ProfileRoot, visible);

        try
        {
            Directory.CreateDirectory(spec.UserDataDir);
            PlaywrightDriverLocator.Prepare();   // 單一檔案發佈時指向解壓出來的 driver（M20）
            _playwright ??= await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);
            var options = new BrowserTypeLaunchPersistentContextOptions
            {
                ExecutablePath = spec.ExecutablePath,
                Headless = spec.Headless,
                Args = spec.Args,
                Proxy = spec.Proxy is null
                    ? null
                    : new Proxy { Server = spec.Proxy.Server, Bypass = spec.Proxy.Bypass, Username = spec.Proxy.Username, Password = spec.Proxy.Password },
                ViewportSize = new ViewportSize { Width = BrowserLimits.ViewportWidth, Height = BrowserLimits.ViewportHeight },
                Locale = "zh-TW",
                AcceptDownloads = true,
                IgnoreHTTPSErrors = false,
                Timeout = 30000,
            };
            var launch = _playwright.Chromium.LaunchPersistentContextAsync(spec.UserDataDir, options);
            IBrowserContext context;
            try
            {
                context = await launch.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 使用者取消時瀏覽器可能還是會啟動完成：完成後立刻關掉，不留下孤兒程序
                _ = launch.ContinueWith(
                    t => t.Result.CloseAsync(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                throw;
            }

            _closeHandler = (_, closed) => OnContextClosed(closed);
            _pageHandler = (_, page) => _ = OnContextPageAsync(page);
            context.Close += _closeHandler;
            context.Page += _pageHandler;
            _context = context;
            _visible = !spec.Headless;
            _spare = context.Pages.FirstOrDefault();
            _idleTimer?.Dispose();
            _idleTimer = _time.CreateTimer(_ => _ = SafeCheckIdleAsync(), null, IdleCheckInterval, IdleCheckInterval);
            Volatile.Write(ref _lastActivityTicks, _time.GetUtcNow().UtcTicks);
            _logger.LogInformation("瀏覽器已啟動：{Browser} {Version}，視窗={Visible}。", browser.DisplayName, browser.Version ?? "?", _visible);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BrowserException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "無法啟動 {Browser}。", browser.DisplayName);
            throw new BrowserException(BrowserFailureKind.LaunchFailed,
                $"無法啟動 {browser.DisplayName}：{FirstLine(ex.Message)}。如果公司的瀏覽器原則禁止自動化，請洽 IT。");
        }
    }

    /// <summary>開一個新分頁；啟動時 Playwright 自帶的 about:blank 先給第一個對話用。</summary>
    private async Task<IPage> NewPageLockedAsync(CancellationToken ct)
    {
        var context = _context ?? throw new BrowserException(BrowserFailureKind.Closed, "瀏覽器已關閉，請重新用 browser_open 開啟。");
        var spare = _spare;
        _spare = null;
        if (spare is { IsClosed: false })
            return spare;
        try
        {
            return await context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogWarning(ex, "無法開啟新分頁。");
            throw new BrowserException(BrowserFailureKind.Other, $"無法開啟新分頁：{FirstLine(ex.Message)}。");
        }
    }

    /// <summary>全部對話合計超過 <see cref="BrowserLimits.MaxPagesTotal"/> 個分頁時，關閉最久沒用的那則對話（不含 <paramref name="keep"/>）。</summary>
    private async Task EvictOverLimitLockedAsync(string keep)
    {
        while (_states.Values.Sum(s => s.Pages.Count) > BrowserLimits.MaxPagesTotal)
        {
            var oldest = _states.Values.Where(s => s.ConversationId != keep).MinBy(s => s.LastUsed);
            if (oldest is null || !_states.TryRemove(oldest.ConversationId, out _))
                return;
            _logger.LogInformation("分頁超過 {Max} 個，關閉最久沒用的對話的分頁。", BrowserLimits.MaxPagesTotal);
            await ClosePagesLockedAsync(oldest).ConfigureAwait(false);
        }
    }

    private async Task ClosePagesLockedAsync(BrowserPageState state)
    {
        var context = _context;
        if (context is null)
            return;
        var pages = state.Pages;
        // 視窗模式下關掉最後一個分頁，瀏覽器程序可能跟著結束：先留一個空白分頁
        if (_spare is not { IsClosed: false } && context.Pages.All(p => pages.Contains(p)))
        {
            try
            {
                _spare = await context.NewPageAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // 瀏覽器已經結束
            }
        }
        foreach (var page in pages)
        {
            try
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // 已經關閉
            }
        }
    }

    /// <summary>關閉瀏覽器程序，Cookie 由 Playwright 寫進設定檔。<paramref name="reason"/> 只用於 log。</summary>
    private async Task CloseContextLockedAsync(string reason, bool keepStates = false)
    {
        _idleTimer?.Dispose();
        _idleTimer = null;
        var context = _context;
        _context = null;
        _spare = null;
        if (context is null)
        {
            if (!keepStates)
                _states.Clear();
            _visible = false;
            return;
        }

        if (_closeHandler is not null)
            context.Close -= _closeHandler;
        if (_pageHandler is not null)
            context.Page -= _pageHandler;
        _closeHandler = null;
        _pageHandler = null;
        try
        {
            await context.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "關閉瀏覽器時發生例外（原因：{Reason}）。", reason);
        }

        // 改成顯示視窗時狀態要保留給呼叫端重新開分頁；其他原因一律清空
        if (!keepStates)
        {
            _states.Clear();
            _visible = false;
        }
        _logger.LogInformation("瀏覽器已關閉：{Reason}。", reason);
    }

    private void DisposePlaywright()
    {
        var playwright = _playwright;
        _playwright = null;
        try
        {
            playwright?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "釋放 Playwright 時發生例外。");
        }
    }

    /// <summary>使用者把瀏覽器視窗關掉（或瀏覽器當掉）：清空狀態，下一次 browser_open 再啟動。</summary>
    private void OnContextClosed(IBrowserContext closed)
    {
        if (!ReferenceEquals(_context, closed))
            return;
        _context = null;
        _spare = null;
        _visible = false;
        _idleTimer?.Dispose();
        _idleTimer = null;
        _states.Clear();
        _logger.LogInformation("瀏覽器已被關閉（視窗被關掉或程序結束）。");
    }

    /// <summary>網站彈出的新分頁：來源是某則對話的分頁時，成為那則對話的目前分頁。Lumi 自己開的分頁沒有來源，直接略過。</summary>
    private async Task OnContextPageAsync(IPage page)
    {
        try
        {
            var opener = await page.OpenerAsync().ConfigureAwait(false);
            if (opener is null)
                return;
            foreach (var state in _states.Values)
            {
                if (state.Owns(opener))
                {
                    state.Adopt(page);
                    state.Touch();
                    return;
                }
            }
        }
        catch (PlaywrightException)
        {
            // 分頁已經關掉
        }
    }

    private async Task SafeCheckIdleAsync()
    {
        try
        {
            await CheckIdleAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "檢查瀏覽器閒置時發生例外。");
        }
    }

    private static async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 3)
                    throw new BrowserException(BrowserFailureKind.Other, "無法刪除瀏覽器的登入狀態，請先關閉所有 Lumi 開啟的瀏覽器視窗再試。");
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim().TrimEnd('。', '.');
        return line.Length == 0 ? "未知的錯誤" : line;
    }
}
