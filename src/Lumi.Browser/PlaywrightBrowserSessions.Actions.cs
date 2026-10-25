using Lumi.Core.Browser;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Lumi.Browser;

// 點擊與輸入（18E）
public sealed partial class PlaywrightBrowserSessions
{
    /// <summary>單一動作（點擊、輸入、選擇…）的逾時（毫秒）。</summary>
    private const float ActionTimeoutMs = 15_000;

    private const int MaxTypeChars = 10_000;
    private const int MaxSelectValues = 20;
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    public async Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct)
    {
        // 點擊之後 ref 會失效，所以先取描述
        var label = DescribeRef(conversationId, @ref) ?? @ref;
        return await ActAsync(conversationId, $"已點擊 {label}。", async page =>
        {
            var element = await ActionLocatorAsync(page, @ref, ct).ConfigureAwait(false);
            try
            {
                await element.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new BrowserException(BrowserFailureKind.Timeout,
                    "點不到這個元素（可能被其他東西遮住或已經消失），請先 browser_snapshot 或 browser_scroll。");
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct)
    {
        if (text.Length > MaxTypeChars)
            throw new BrowserException(BrowserFailureKind.TooLarge, $"要輸入的文字太長（超過 {MaxTypeChars:N0} 字）。");

        var label = DescribeRef(conversationId, @ref) ?? @ref;
        // 訊息與 log 只寫字數，不寫輸入的內容
        return await ActAsync(conversationId, $"已在 {label} 輸入 {text.Length} 個字。", async page =>
        {
            var element = await ActionLocatorAsync(page, @ref, ct).ConfigureAwait(false);
            if (await IsPasswordFieldAsync(element, ct).ConfigureAwait(false))
                throw new BrowserException(BrowserFailureKind.PasswordField, "這是密碼欄位，Lumi 不會替你輸入密碼。請用 browser_wait_for_user 讓使用者自己輸入。");

            if (replace)
                await element.FillAsync(text, new LocatorFillOptions { Timeout = ActionTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
            else
                await element.PressSequentiallyAsync(text, new LocatorPressSequentiallyOptions { Timeout = ActionTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);

            if (submit)
                await element.PressAsync("Enter", new LocatorPressOptions { Timeout = ActionTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct)
    {
        if (values.Count is 0 or > MaxSelectValues)
            throw new BrowserException(BrowserFailureKind.Other, $"values 要有 1 到 {MaxSelectValues} 個選項。");

        return await ActAsync(conversationId, $"已選擇 {values.Count} 個選項。", async page =>
        {
            var element = await ActionLocatorAsync(page, @ref, ct).ConfigureAwait(false);
            // SelectOptionAsync 的字串同時比對 value 與 label
            await element.SelectOptionAsync(values, new LocatorSelectOptionOptions { Timeout = ActionTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct)
    {
        if (!BrowserRules.TryParseKey(key, out var parsed, out var error))
            throw new BrowserException(BrowserFailureKind.Other, error);

        return await ActAsync(conversationId, $"已按下 {parsed}。", async page =>
        {
            await page.Keyboard.PressAsync(parsed).WaitAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new BrowserException(BrowserFailureKind.Other, "找不到要上傳的檔案。");
        if (info.Length > MaxUploadBytes)
            throw new BrowserException(BrowserFailureKind.TooLarge, $"檔案太大（超過 {MaxUploadBytes / (1024 * 1024)} MB），無法上傳。");

        return await ActAsync(conversationId, $"已選擇上傳檔案 {info.Name}。", async page =>
        {
            var element = await ActionLocatorAsync(page, @ref, ct).ConfigureAwait(false);
            if (await IsFileInputAsync(element, ct).ConfigureAwait(false))
            {
                await element.SetInputFilesAsync(info.FullName, new LocatorSetInputFilesOptions { Timeout = ActionTimeoutMs })
                    .WaitAsync(ct).ConfigureAwait(false);
                return;
            }

            // 不是 <input type=file>：點它，等它開出檔案選擇器
            IFileChooser chooser;
            try
            {
                chooser = await page.RunAndWaitForFileChooserAsync(
                    () => element.ClickAsync(new LocatorClickOptions { Timeout = ActionTimeoutMs }),
                    new PageRunAndWaitForFileChooserOptions { Timeout = ActionTimeoutMs }).WaitAsync(ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new BrowserException(BrowserFailureKind.Timeout, "這個元素沒有跳出選擇檔案的視窗，請確認它是上傳按鈕。");
            }
            await chooser.SetFilesAsync(info.FullName, new FileChooserSetFilesOptions { Timeout = ActionTimeoutMs })
                .WaitAsync(ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 動作類方法的共同包裝：取得這則對話的狀態、排隊、執行動作、等頁面穩定，
    /// 回「訊息＋對話框／新分頁等提示＋新快照」（D4）。
    /// </summary>
    private async Task<BrowserOutcome> ActAsync(string conversationId, string message, Func<IPage, Task> action, CancellationToken ct)
    {
        var state = await _host.GetStateAsync(conversationId, create: false, ct).ConfigureAwait(false);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action(state.Page).ConfigureAwait(false);
            await state.SettleAsync(ct).ConfigureAwait(false);

            var lines = new List<string> { message };
            lines.AddRange(state.TakeNotes().Split('\n'));
            var text = string.Join('\n', lines.Where(line => !string.IsNullOrWhiteSpace(line)));
            var snapshot = await state.CaptureAsync(BrowserLimits.SnapshotDefaultChars, ct).ConfigureAwait(false);
            return new BrowserOutcome(text, snapshot);
        }
        catch (Exception ex) when (ex is not (BrowserException or OperationCanceledException))
        {
            throw ToActionException(ex);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>依 ref 取得元素；目前分頁找不到（頁面已經變了）丟 <see cref="BrowserFailureKind.StaleRef"/>。</summary>
    private static async Task<ILocator> ActionLocatorAsync(IPage page, string @ref, CancellationToken ct)
    {
        var locator = page.Locator("aria-ref=" + @ref);
        if (await locator.CountAsync().WaitAsync(ct).ConfigureAwait(false) == 0)
            throw new BrowserException(BrowserFailureKind.StaleRef, $"找不到 {@ref}，頁面可能已經變了，請先 browser_snapshot。");
        return locator;
    }

    /// <summary>D7：<c>type=password</c>，或 <c>autocomplete</c> 含 <c>current-password</c>／<c>new-password</c>。</summary>
    private static async Task<bool> IsPasswordFieldAsync(ILocator element, CancellationToken ct)
    {
        var type = await element.GetAttributeAsync("type", new LocatorGetAttributeOptions { Timeout = ActionTimeoutMs })
            .WaitAsync(ct).ConfigureAwait(false);
        if (string.Equals(type?.Trim(), "password", StringComparison.OrdinalIgnoreCase))
            return true;

        var autocomplete = await element.GetAttributeAsync("autocomplete", new LocatorGetAttributeOptions { Timeout = ActionTimeoutMs })
            .WaitAsync(ct).ConfigureAwait(false);
        return autocomplete is not null
            && (autocomplete.Contains("current-password", StringComparison.OrdinalIgnoreCase)
                || autocomplete.Contains("new-password", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsFileInputAsync(ILocator element, CancellationToken ct)
    {
        // 固定的 JavaScript，不含任何來自模型的內容
        return await element.EvaluateAsync<bool>("e => e instanceof HTMLInputElement && e.type === 'file'")
            .WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把 Playwright 的例外轉成給模型看的 <see cref="BrowserException"/>。
    /// Debug log 只寫例外型別與訊息的第一行：Playwright 的 call log 會把 <c>fill("…")</c> 的內容寫進後面幾行，
    /// 完整例外會洩漏輸入的文字（也可能含網址）。
    /// </summary>
    private BrowserException ToActionException(Exception ex)
    {
        var firstLine = ex.Message.Split('\n', 2)[0].Trim();
        if (firstLine.Contains("://", StringComparison.Ordinal))
            firstLine = "";
        _logger.LogDebug("瀏覽器動作失敗：{Type} {Reason}", ex.GetType().Name, firstLine.Length > 200 ? firstLine[..200] : firstLine);
        if (ex is TimeoutException)
            return new BrowserException(BrowserFailureKind.Timeout, "操作逾時。");

        if (ex is PlaywrightException)
        {
            var code = ExtractActionNetError(ex.Message);
            if (code is not null)
                return new BrowserException(BrowserFailureKind.Navigation, $"網頁開啟失敗：{code}。");
        }

        return new BrowserException(BrowserFailureKind.Other, "瀏覽器操作失敗。");
    }

    /// <summary>取出訊息裡 <c>net::ERR_XXX</c> 那段；沒有時 null。</summary>
    private static string? ExtractActionNetError(string message)
    {
        var start = message.IndexOf("net::ERR_", StringComparison.Ordinal);
        if (start < 0)
            return null;
        var end = start + "net::ERR_".Length;
        while (end < message.Length && (char.IsAsciiLetterUpper(message[end]) || char.IsAsciiDigit(message[end]) || message[end] == '_'))
            end++;
        return message[start..end];
    }
}
