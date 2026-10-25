using System.Text.RegularExpressions;
using Lumi.Core.Browser;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Lumi.Browser;

// 下載與等待使用者（18F）
public sealed partial class PlaywrightBrowserSessions
{
    /// <summary>點擊之後等下載開始的毫秒數。測試會縮短它（見 BrowserTransferTests）。</summary>
    internal static int DownloadWaitMilliseconds = 60_000;

    private const float TransferActionTimeoutMs = 15_000;
    private static readonly TimeSpan WaitPollInterval = TimeSpan.FromSeconds(1);

    public async Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        var temp = destinationPath + ".lumi-download";
        var moved = false;
        try
        {
            var page = state.Page;
            var locator = page.Locator("aria-ref=" + @ref);
            if (await locator.CountAsync().WaitAsync(ct).ConfigureAwait(false) == 0)
                throw new BrowserException(BrowserFailureKind.StaleRef, $"找不到 {@ref}，頁面可能已經變了，請先 browser_snapshot。");

            // 點擊本身逾時（被遮住）與「點了但沒有下載」都是 Playwright 的 TimeoutException，用旗標分辨
            var clicked = false;
            IDownload download;
            try
            {
                download = await page.RunAndWaitForDownloadAsync(async () =>
                {
                    await locator.ClickAsync(new LocatorClickOptions { Timeout = TransferActionTimeoutMs }).ConfigureAwait(false);
                    clicked = true;
                }, new PageRunAndWaitForDownloadOptions { Timeout = DownloadWaitMilliseconds }).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw clicked
                    ? new BrowserException(BrowserFailureKind.Timeout, "點擊後沒有開始下載。")
                    : new BrowserException(BrowserFailureKind.Timeout, "點不到這個元素（可能被其他東西遮住或已經消失），請先 browser_snapshot 或 browser_scroll。");
            }

            try
            {
                await download.SaveAsAsync(temp).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                throw new BrowserException(BrowserFailureKind.Other, "下載失敗，檔案沒有存下來。");
            }
            finally
            {
                await DeleteBrowserCopyAsync(download).ConfigureAwait(false);
            }

            var bytes = new FileInfo(temp).Length;
            if (bytes > maxBytes)
                throw new BrowserException(BrowserFailureKind.TooLarge, $"下載的檔案太大（上限 {FormatTransferLimit(maxBytes)}），已刪除。");

            try
            {
                File.Move(temp, destinationPath, overwrite: false);
                moved = true;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                throw new BrowserException(BrowserFailureKind.Other, "目的地已經有同名檔案，沒有覆蓋。");
            }

            state.Touch();
            return new BrowserDownload(download.SuggestedFilename, bytes);
        }
        catch (Exception ex) when (ex is not (BrowserException or OperationCanceledException))
        {
            throw ConvertTransferException(ex);
        }
        finally
        {
            if (!moved)
                DeleteTransferTempQuietly(temp);
            state.Gate.Release();
        }
    }

    public async Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct)
    {
        var urlPart = string.IsNullOrWhiteSpace(condition.UrlContains) ? null : condition.UrlContains;
        var text = string.IsNullOrWhiteSpace(condition.TextVisible) ? null : condition.TextVisible;
        if (urlPart is null && text is null)
            throw new BrowserException(BrowserFailureKind.Other, "請指定完成條件（網址包含的文字，或頁面出現的文字）。");

        // 先確認這則對話有分頁，免得白白把瀏覽器重啟成可見視窗
        await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        await _host.EnsureVisibleAsync(ct).ConfigureAwait(false);
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);

        var started = _time.GetTimestamp();
        var completed = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                state.Touch();
                if (!_host.IsRunning || state.PageOrNull is not { } page)
                    throw new BrowserException(BrowserFailureKind.Closed, "瀏覽器視窗被關閉了。");

                if (await IsWaitConditionMetAsync(page, urlPart, text, ct).ConfigureAwait(false))
                {
                    completed = true;
                    break;
                }
                if (_time.GetElapsedTime(started) >= timeout)
                    break;
                await Task.Delay(WaitPollInterval, _time, ct).ConfigureAwait(false);
            }

            // 等待期間沒有持有 Gate；擷取快照時才排隊
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_host.IsRunning || state.PageOrNull is null)
                    throw new BrowserException(BrowserFailureKind.Closed, "瀏覽器視窗被關閉了。");
                await state.SettleAsync(ct).ConfigureAwait(false);
                var snapshot = await state.CaptureAsync(BrowserLimits.SnapshotDefaultChars, ct).ConfigureAwait(false);
                state.Touch();
                var message = completed
                    ? "使用者已完成操作。"
                    : $"等了 {(int)Math.Ceiling(timeout.TotalSeconds)} 秒，使用者還沒有完成。";
                var notes = state.TakeNotes();
                return new BrowserWaitResult(completed, notes.Length == 0 ? message : message + "\n" + notes, snapshot);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        catch (Exception ex) when (ex is not (BrowserException or OperationCanceledException))
        {
            throw ConvertTransferException(ex);
        }
    }

    /// <summary>檢查完成條件。頁面正在跳轉、分頁剛被關掉這類暫時的錯誤當作「還沒完成」，下一輪再看。</summary>
    private static async Task<bool> IsWaitConditionMetAsync(IPage page, string? urlPart, string? text, CancellationToken ct)
    {
        try
        {
            if (urlPart is not null && page.Url.Contains(urlPart, StringComparison.OrdinalIgnoreCase))
                return true;
            if (text is null)
                return false;
            var count = page.GetByText(text).Filter(new LocatorFilterOptions { Visible = true }).CountAsync();
            return await count.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false) > 0;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            return false;
        }
    }

    private BrowserException ConvertTransferException(Exception ex)
    {
        // 例外訊息可能含網址，log 只留類型
        _logger.LogDebug("瀏覽器傳輸操作失敗：{Type}", ex.GetType().Name);
        if (ex is TimeoutException)
            return new BrowserException(BrowserFailureKind.Timeout, "操作逾時。");
        if (ex is PlaywrightException)
        {
            var match = Regex.Match(ex.Message, @"net::ERR_[A-Z0-9_]+");
            if (match.Success)
                return new BrowserException(BrowserFailureKind.Navigation, $"網頁開啟失敗：{match.Value}。");
        }
        return new BrowserException(BrowserFailureKind.Other, "瀏覽器操作失敗。");
    }

    private static string FormatTransferLimit(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024 * 1024)} MB" : $"{bytes} 位元組";

    private static async Task DeleteBrowserCopyAsync(IDownload download)
    {
        try
        {
            await download.DeleteAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // 瀏覽器那一份暫存檔刪不掉不影響結果
        }
    }

    private static void DeleteTransferTempQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暫存檔刪不掉就留著，不蓋過原本的錯誤
        }
    }
}
