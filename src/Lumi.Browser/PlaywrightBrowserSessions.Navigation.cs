using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Browser;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Lumi.Browser;

// 導覽與讀取（18D）
public sealed partial class PlaywrightBrowserSessions
{
    // 固定的 JavaScript：不含任何使用者或模型的內容。
    private const string OuterHtmlScript = "e => e.outerHTML";

    private const string TablesScript =
        "e => (e.matches('table') ? [e] : [...e.querySelectorAll('table')]).slice(0, 10).map(t => ({ caption: t.caption ? t.caption.innerText.trim() : null, "
        + "rows: [...t.rows].slice(0, 200).map(r => [...r.cells].map(c => c.innerText.trim().replace(/\\s+/g, ' ').slice(0, 200))) }))";

    private const string PageSizeScript = "() => [document.documentElement.scrollWidth, document.documentElement.scrollHeight]";

    private static readonly Regex NetErrorPattern = new(@"net::ERR_[A-Z0-9_]+", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(@"\w+://\S+", RegexOptions.Compiled);

    /// <summary>ref 只會長成 e12 或 f1e2；不符合的一律當作找不到，避免模型的字串進到選取器。</summary>
    private static readonly Regex RefPattern = new(@"^(f\d+)?e\d+$", RegexOptions.Compiled);

    public async Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            throw new BrowserException(BrowserFailureKind.Other, "只能開啟 http 或 https 開頭的網址。");

        var state = await _host.GetStateAsync(conversationId, create: true, ct).ConfigureAwait(false);
        return await RunGatedAsync(state, async () =>
        {
            var response = await state.Page
                .GotoAsync(url.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavigationTimeoutMs() })
                .WaitAsync(ct).ConfigureAwait(false);
            await state.SettleAsync(ct).ConfigureAwait(false);

            var message = $"已開啟 {url.Host}{url.AbsolutePath}。";
            if (response is { Status: >= 400 })
                message += $"（伺服器回應 {response.Status}）";
            return await FinishAsync(state, message, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        return await RunGatedAsync(state, async () =>
        {
            _ = state.Page;
            await state.SettleAsync(ct).ConfigureAwait(false);
            return await state.CaptureAsync(BrowserRules.TryParseSnapshotChars(maxChars), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        return await RunGatedAsync(state, async () =>
        {
            var page = state.Page;
            string html;
            string? text = null;
            if (@ref is null)
            {
                html = await page.ContentAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                var locator = await FindRefAsync(page, @ref, ct).ConfigureAwait(false);
                html = await locator.EvaluateAsync<string>(OuterHtmlScript).WaitAsync(ct).ConfigureAwait(false);
                text = await locator.InnerTextAsync().WaitAsync(ct).ConfigureAwait(false);
            }

            if (Encoding.UTF8.GetByteCount(html) > WebLimits.PageMaxBytes)
                throw new BrowserException(BrowserFailureKind.TooLarge, "網頁內容太大（超過 5 MB），請改用 browser_snapshot 或指定元素。");

            var title = await page.TitleAsync().WaitAsync(ct).ConfigureAwait(false);
            return new BrowserPageContent(
                BrowserPageState.ToUri(page.Url), string.IsNullOrWhiteSpace(title) ? null : title, html, text);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        return await RunGatedAsync<IReadOnlyList<BrowserTable>>(state, async () =>
        {
            var page = state.Page;
            var scope = @ref is null
                ? page.Locator("html")
                : await FindRefAsync(page, @ref, ct).ConfigureAwait(false);
            var raw = await scope.EvaluateAsync<TableData[]?>(TablesScript).WaitAsync(ct).ConfigureAwait(false);
            return (raw ?? [])
                .Select(t => new BrowserTable(
                    string.IsNullOrWhiteSpace(t.Caption) ? null : t.Caption,
                    (t.Rows ?? []).Select(r => (IReadOnlyList<string>)(r ?? [])).ToArray()))
                .ToArray();
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        return await RunGatedAsync(state, async () =>
        {
            var page = state.Page;
            var width = BrowserLimits.ViewportWidth;
            var height = BrowserLimits.ViewportHeight;
            var options = new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 70, FullPage = fullPage };
            if (fullPage)
            {
                var size = await page.EvaluateAsync<int[]>(PageSizeScript).WaitAsync(ct).ConfigureAwait(false);
                width = Math.Max(BrowserLimits.ViewportWidth, size[0]);
                height = Math.Max(BrowserLimits.ViewportHeight, size[1]);
                if (height > BrowserLimits.FullPageMaxHeight)
                {
                    height = BrowserLimits.FullPageMaxHeight;
                    width = BrowserLimits.ViewportWidth;
                    options.Clip = new Clip { X = 0, Y = 0, Width = width, Height = height };
                }
            }

            var data = await page.ScreenshotAsync(options).WaitAsync(ct).ConfigureAwait(false);
            if (data.LongLength > BrowserLimits.MaxScreenshotBytes)
                throw new BrowserException(BrowserFailureKind.TooLarge, "截圖太大（超過 5 MB），請改用 browser_snapshot。");
            return new BrowserShot(data, "image/jpeg", width, height);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        return await RunGatedAsync(state, async () =>
        {
            var page = state.Page;
            string message;
            if (@ref is not null)
            {
                var locator = await FindRefAsync(page, @ref, ct).ConfigureAwait(false);
                await locator.ScrollIntoViewIfNeededAsync(new LocatorScrollIntoViewIfNeededOptions { Timeout = 5000 })
                    .WaitAsync(ct).ConfigureAwait(false);
                message = $"已捲動到 {state.RefLabels.GetValueOrDefault(@ref) ?? @ref}。";
            }
            else
            {
                var step = (float)(BrowserLimits.ViewportHeight * 0.8);
                switch (direction)
                {
                    case BrowserScrollDirection.Up:
                        await page.Mouse.WheelAsync(0, -step).WaitAsync(ct).ConfigureAwait(false);
                        message = "已往上捲動。";
                        break;
                    case BrowserScrollDirection.Down:
                        await page.Mouse.WheelAsync(0, step).WaitAsync(ct).ConfigureAwait(false);
                        message = "已往下捲動。";
                        break;
                    case BrowserScrollDirection.Top:
                        await page.EvaluateAsync("window.scrollTo(0, 0)").WaitAsync(ct).ConfigureAwait(false);
                        message = "已捲動到頁面頂端。";
                        break;
                    default:
                        await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)").WaitAsync(ct).ConfigureAwait(false);
                        message = "已捲動到頁面底端。";
                        break;
                }
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
            return await FinishAsync(state, message, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        return await RunGatedAsync(state, async () =>
        {
            var urlBefore = state.Page.Url;
            var response = await state.Page
                .GoBackAsync(new PageGoBackOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavigationTimeoutMs() })
                .WaitAsync(ct).ConfigureAwait(false);
            // 沒有上一頁時回 null；回到沒有回應的頁面（例如 about:blank）也是 null，所以再看網址有沒有變。
            if (response is null && state.Page.Url == urlBefore)
                throw new BrowserException(BrowserFailureKind.Other, "沒有上一頁。");
            await state.SettleAsync(ct).ConfigureAwait(false);
            return await FinishAsync(state, "已回到上一頁。", ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    // ---- 共用 ----

    private float NavigationTimeoutMs() => Math.Max(1, _catalog.Current.Network.TimeoutSeconds) * 1000f;

    /// <summary>訊息加上這次累積的提示（對話框、新分頁），再附上新的快照。</summary>
    private static async Task<BrowserOutcome> FinishAsync(BrowserPageState state, string message, CancellationToken ct)
    {
        var notes = state.TakeNotes();
        var snapshot = await state.CaptureAsync(BrowserLimits.SnapshotDefaultChars, ct).ConfigureAwait(false);
        return new BrowserOutcome(notes.Length == 0 ? message : message + "\n" + notes, snapshot);
    }

    private static async Task<ILocator> FindRefAsync(IPage page, string @ref, CancellationToken ct)
    {
        if (RefPattern.IsMatch(@ref))
        {
            var locator = page.Locator("aria-ref=" + @ref);
            try
            {
                if (await locator.CountAsync().WaitAsync(ct).ConfigureAwait(false) > 0)
                    return locator;
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("aria-ref", StringComparison.Ordinal))
            {
                // 例如「Invalid frame in aria-ref selector」：快照裡沒有這個 iframe，照找不到處理
            }
        }

        throw new BrowserException(BrowserFailureKind.StaleRef, $"找不到 {@ref}，頁面可能已經變了，請先 browser_snapshot。");
    }

    /// <summary>同一則對話的操作排隊，並把 Playwright 的例外轉成 <see cref="BrowserException"/>。</summary>
    private async Task<T> RunGatedAsync<T>(BrowserPageState state, Func<Task<T>> work, CancellationToken ct)
    {
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            state.Touch();
            return await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (BrowserException or OperationCanceledException))
        {
            throw Translate(ex);
        }
        finally
        {
            state.Touch();
            state.Gate.Release();
        }
    }

    private BrowserException Translate(Exception ex)
    {
        _logger.LogDebug("瀏覽器操作失敗：{Type} {Message}", ex.GetType().Name, Scrub(ex.Message));
        // Playwright 的逾時在 .NET 是 System.TimeoutException；保險起見也認「Timeout …ms exceeded」開頭的 PlaywrightException。
        if (ex is TimeoutException || (ex is PlaywrightException && ex.Message.StartsWith("Timeout", StringComparison.Ordinal)))
            return new BrowserException(BrowserFailureKind.Timeout, "操作逾時。");
        if (ex is PlaywrightException)
        {
            var match = NetErrorPattern.Match(ex.Message);
            if (match.Success)
                return new BrowserException(BrowserFailureKind.Navigation, $"網頁開啟失敗：{match.Value}。");
        }

        return new BrowserException(BrowserFailureKind.Other, "瀏覽器操作失敗。");
    }

    /// <summary>log 只留例外訊息的第一行，並拿掉網址。</summary>
    private static string Scrub(string message)
    {
        var line = message.Split('\n', 2)[0];
        return UrlPattern.Replace(line, "<url>");
    }

    private sealed class TableData
    {
        [System.Text.Json.Serialization.JsonPropertyName("caption")]
        public string? Caption { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("rows")]
        public string[][]? Rows { get; set; }
    }
}
