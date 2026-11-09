using System.Reflection;
using Lumi.Core.Browser;
using Lumi.Core.Diagrams;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Lumi.Browser;

/// <summary>
/// 用電腦上已安裝的 Edge／Chrome（無頭、離線）執行內建的 mermaid.js，把流程圖畫成 PNG。
/// 和 M18 的瀏覽器工具分開：自己的無頭瀏覽器、不用使用者的設定檔、所有網路請求一律擋掉，圖的內容不會離開這台電腦。
/// 第一次畫圖才啟動，閒置 <see cref="IdleTimeout"/> 後關閉；一次只畫一張。
/// log 只寫結果與耗時，不寫圖的內容。
/// </summary>
public sealed class MermaidRenderer : IDiagramRenderer, IAsyncDisposable
{
    /// <summary>程式碼最多幾個字。</summary>
    public const int MaxCodeLength = 20_000;

    /// <summary>一張圖最多畫多久。</summary>
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(20);

    /// <summary>閒置多久關閉無頭瀏覽器。</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    internal const string NoBrowserProblem = "要顯示流程圖，這台電腦需要安裝 Microsoft Edge 或 Google Chrome。";
    internal const string TooLongProblem = "流程圖太長，無法畫出。";
    internal const string TimeoutProblem = "畫流程圖逾時。";
    internal const string FailedProblem = "畫流程圖時發生錯誤。";

    private const string ScriptResource = "Lumi.Browser.mermaid.min.js";

    private readonly IBrowserLocator _locator;
    private readonly ILogger<MermaidRenderer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private ITimer? _idleTimer;
    private readonly TimeProvider _time;
    private bool _disposed;

    public MermaidRenderer(IBrowserLocator locator, ILogger<MermaidRenderer> logger, TimeProvider? time = null)
    {
        _locator = locator;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<DiagramRenderResult> RenderMermaidAsync(string code, bool dark, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaxCodeLength)
            return DiagramRenderResult.Fail(TooLongProblem);
        if (_locator.Resolve(BrowserChoice.Auto).Selected is not { } browser)
            return DiagramRenderResult.Fail(NoBrowserProblem);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var started = _time.GetTimestamp();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RenderTimeout);

            var page = await EnsurePageAsync(browser, timeout.Token).ConfigureAwait(false);
            var error = await page.EvaluateAsync<string>(RenderScript, new { code, theme = dark ? "dark" : "default" })
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(error))
            {
                _logger.LogInformation("流程圖語法有誤，改顯示程式碼。");
                return DiagramRenderResult.Fail("流程圖的語法有誤：" + FirstLine(error));
            }

            var element = await page.QuerySelectorAsync("#out svg").WaitAsync(timeout.Token).ConfigureAwait(false);
            if (element is null || await element.BoundingBoxAsync().WaitAsync(timeout.Token).ConfigureAwait(false) is not { } box)
                return DiagramRenderResult.Fail(FailedProblem);
            var png = await element.ScreenshotAsync(new ElementHandleScreenshotOptions { Type = ScreenshotType.Png })
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            _logger.LogInformation("畫好流程圖，花費 {Ms} 毫秒。", (int)_time.GetElapsedTime(started).TotalMilliseconds);
            return DiagramRenderResult.Ok(new DiagramImage(png, box.Width, box.Height));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("畫流程圖逾時，關閉無頭瀏覽器。");
            await CloseAsync().ConfigureAwait(false);
            return DiagramRenderResult.Fail(TimeoutProblem);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "畫流程圖失敗，關閉無頭瀏覽器。");
            await CloseAsync().ConfigureAwait(false);
            return DiagramRenderResult.Fail(FailedProblem);
        }
        finally
        {
            if (!_disposed)
            {
                _idleTimer ??= _time.CreateTimer(_ => _ = CloseWhenIdleAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
            _gate.Release();
        }
    }

    /// <summary>
    /// mermaid 在頁面裡畫圖：成功回空字串、失敗回錯誤訊息。securityLevel=strict：不執行圖裡的連結與 HTML。
    /// 每次都重新 initialize，淺色與深色可以交替。
    /// </summary>
    private const string RenderScript = """
        async ({ code, theme }) => {
          const out = document.getElementById('out');
          out.innerHTML = '';
          try {
            mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme, fontFamily: 'sans-serif' });
            const { svg } = await mermaid.render('lumi-diagram-' + Date.now(), code);
            out.innerHTML = svg;
            return '';
          } catch (e) {
            document.querySelectorAll('[id^="dlumi-diagram-"]').forEach(n => n.remove());
            return String((e && e.message) || e || 'error');
          }
        }
        """;

    private async Task<IPage> EnsurePageAsync(InstalledBrowser browser, CancellationToken ct)
    {
        if (_page is { IsClosed: false } page)
            return page;

        PlaywrightDriverLocator.Prepare();   // 單一檔案發佈時指向解壓出來的 driver（M20）
        _playwright ??= await Playwright.CreateAsync().WaitAsync(ct).ConfigureAwait(false);
        _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            ExecutablePath = browser.Path,
            Headless = true,
        }).WaitAsync(ct).ConfigureAwait(false);

        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            DeviceScaleFactor = 2,
            Offline = true,
            ViewportSize = new ViewportSize { Width = 1200, Height = 800 },
        }).WaitAsync(ct).ConfigureAwait(false);
        // 頁面只用內嵌的腳本，任何對外請求（字型、圖片、連結）一律擋掉
        await context.RouteAsync("**/*", route => route.AbortAsync()).WaitAsync(ct).ConfigureAwait(false);

        page = await context.NewPageAsync().WaitAsync(ct).ConfigureAwait(false);
        await page.SetContentAsync(PageHtml()).WaitAsync(ct).ConfigureAwait(false);
        _page = page;
        return page;
    }

    private static string? _pageHtml;

    private static string PageHtml()
    {
        if (_pageHtml is not null)
            return _pageHtml;
        using var stream = typeof(MermaidRenderer).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("找不到內建的 mermaid.js。");
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();
        // 背景透明：截圖只留圖本身；外距讓邊框不被切掉
        return _pageHtml = "<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{margin:0;background:transparent}" +
            "#out{display:inline-block;padding:8px}</style></head><body><div id=\"out\"></div><script>" + script + "</script></body></html>";
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? text;
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    private async Task CloseWhenIdleAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
            return;   // 正在畫：畫完會重新計時
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CloseAsync()
    {
        _page = null;
        try
        {
            if (_browser is not null)
                await _browser.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "關閉畫圖用的瀏覽器失敗。");
        }
        _browser = null;
        _playwright?.Dispose();
        _playwright = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _idleTimer?.Dispose();
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
