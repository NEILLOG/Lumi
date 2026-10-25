using System.Collections.Concurrent;
using System.Reflection;
using Lumi.Browser.Tests.TestSupport;
using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace Lumi.Browser.Tests;

/// <summary>
/// 點擊與輸入（18E）的測試。全部要真瀏覽器：<c>LUMI_BROWSER_TESTS=1</c>。
/// 18D 合併之前沒有 <c>OpenAsync</c> 可用，所以用 <see cref="OpenAsync"/> 直接把分頁導向測試頁並擷取快照（更新 ref 描述表）。
/// </summary>
public sealed class BrowserActionTests : IClassFixture<BrowserActionTests.RigFixture>
{
    private readonly BrowserTestRig _rig;

    public BrowserActionTests(RigFixture fixture) => _rig = fixture.Rig;

    public sealed class RigFixture : IDisposable
    {
        internal BrowserTestRig Rig { get; } = new();

        public void Dispose() => Rig.Dispose();
    }

    // ---- 輔助 ----

    private static readonly FieldInfo HostField =
        typeof(PlaywrightBrowserSessions).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>開一則新對話並導向 <paramref name="path"/>，擷取快照。</summary>
    private static async Task<(string Id, BrowserPageState State)> OpenAsync(PlaywrightBrowserSessions sessions, Uri url)
    {
        var id = "c" + Guid.NewGuid().ToString("N");
        var host = (BrowserHost)HostField.GetValue(sessions)!;
        var state = await host.GetStateAsync(id, create: true, default);
        await state.Page.GotoAsync(url.AbsoluteUri);
        await state.CaptureAsync(BrowserLimits.SnapshotDefaultChars, default);
        return (id, state);
    }

    private Task<(string Id, BrowserPageState State)> OpenAsync(string path) => OpenAsync(_rig.Sessions, _rig.Url(path));

    /// <summary>在目前頁面末端加一段固定的 HTML，再重新擷取快照。</summary>
    private static async Task InjectAsync(BrowserPageState state, string html)
    {
        await state.Page.EvaluateAsync("h => document.body.insertAdjacentHTML('beforeend', h)", html);
        await state.CaptureAsync(BrowserLimits.SnapshotDefaultChars, default);
    }

    /// <summary>找描述裡有 <c>"名稱"</c> 的 ref（可再用 <paramref name="role"/> 限定，例如 button、textbox）。</summary>
    private static string RefOf(BrowserPageState state, string name, string? role = null)
    {
        var hit = state.RefLabels.FirstOrDefault(kv =>
            kv.Value.Contains($"\"{name}\"", StringComparison.Ordinal) && (role is null || kv.Value.StartsWith(role, StringComparison.Ordinal)));
        Assert.True(hit.Key is not null, $"找不到 {role} \"{name}\"；現有：{string.Join("; ", state.RefLabels.Select(kv => kv.Key + "=" + kv.Value))}");
        return hit.Key;
    }

    private static Task<string> ValueAsync(BrowserPageState state, string selector) =>
        state.Page.Locator(selector).InputValueAsync();

    // ---- 點擊 ----

    [RealBrowserFact]
    public async Task Click_changes_title_and_returns_new_snapshot()
    {
        var (id, state) = await OpenAsync("/");

        var outcome = await _rig.Sessions.ClickAsync(id, RefOf(state, "送出", "button"), default);

        Assert.Equal("clicked", await state.Page.TitleAsync());
        Assert.Equal("clicked", outcome.Snapshot.Title);
        Assert.Contains("已點擊 button \"送出\"", outcome.Message);
        Assert.Contains("[ref=", outcome.Snapshot.Text);
    }

    [RealBrowserFact]
    public async Task Click_inside_iframe_works()
    {
        var (id, state) = await OpenAsync("/");
        var frameButton = RefOf(state, "框內按鈕");
        Assert.StartsWith("f", frameButton);

        var outcome = await _rig.Sessions.ClickAsync(id, frameButton, default);

        Assert.Contains("已點擊", outcome.Message);
    }

    [RealBrowserFact]
    public async Task Click_with_stale_ref_throws_StaleRef()
    {
        var (id, state) = await OpenAsync("/");
        var submit = RefOf(state, "送出", "button");

        await state.Page.GotoAsync(_rig.Url("/tall").AbsoluteUri);

        var ex = await Assert.ThrowsAsync<BrowserException>(() => _rig.Sessions.ClickAsync(id, submit, default));
        Assert.Equal(BrowserFailureKind.StaleRef, ex.Kind);
        Assert.Contains(submit, ex.Message);
        Assert.Contains("browser_snapshot", ex.Message);
    }

    [RealBrowserFact]
    public async Task Click_without_open_page_throws_NoPage()
    {
        var ex = await Assert.ThrowsAsync<BrowserException>(() => _rig.Sessions.ClickAsync("never-opened", "e1", default));
        Assert.Equal(BrowserFailureKind.NoPage, ex.Kind);
    }

    [RealBrowserFact]
    public async Task Click_on_new_tab_link_switches_current_page_and_notes_it()
    {
        var (id, state) = await OpenAsync("/");
        var before = state.Page;

        var outcome = await _rig.Sessions.ClickAsync(id, RefOf(state, "新分頁", "link"), default);

        Assert.Contains("新分頁", outcome.Message);
        Assert.NotSame(before, state.Page);
        Assert.Contains("/whoami", state.Page.Url);
        Assert.Contains("/whoami", outcome.Snapshot.Url.AbsoluteUri);
    }

    [RealBrowserFact]
    public async Task Click_that_raises_dialog_notes_it_and_continues()
    {
        var (id, state) = await OpenAsync("/");

        var outcome = await _rig.Sessions.ClickAsync(id, RefOf(state, "跳對話框", "button"), default);

        Assert.Contains("對話框「hi」", outcome.Message);
        Assert.Contains("已取消", outcome.Message);
        Assert.Contains("[ref=", outcome.Snapshot.Text);
    }

    [RealBrowserFact]
    public async Task Click_on_covered_element_times_out_with_hint()
    {
        var (id, state) = await OpenAsync("/");
        var submit = RefOf(state, "送出", "button");
        // 蓋一層全螢幕的透明遮罩，點擊會一直等到逾時
        await state.Page.EvaluateAsync(
            "() => { const d = document.createElement('div'); d.style.cssText = 'position:fixed;inset:0;z-index:99999'; document.body.appendChild(d); }");

        var ex = await Assert.ThrowsAsync<BrowserException>(() => _rig.Sessions.ClickAsync(id, submit, default));

        Assert.Equal(BrowserFailureKind.Timeout, ex.Kind);
        Assert.Contains("點不到這個元素", ex.Message);
    }

    // ---- 輸入 ----

    [RealBrowserFact]
    public async Task Type_replace_sets_value_and_append_keeps_existing_text()
    {
        var (id, state) = await OpenAsync("/");
        var name = RefOf(state, "姓名", "textbox");

        var first = await _rig.Sessions.TypeAsync(id, name, "小明", replace: true, submit: false, default);
        Assert.Equal("小明", await ValueAsync(state, "input[aria-label=姓名]"));
        Assert.Contains("輸入 2 個字", first.Message);

        // 輸入後快照重新編號，用新快照的 ref
        name = RefOf(state, "姓名", "textbox");
        await _rig.Sessions.TypeAsync(id, name, "同學", replace: false, submit: false, default);
        Assert.Equal("小明同學", await ValueAsync(state, "input[aria-label=姓名]"));

        name = RefOf(state, "姓名", "textbox");
        await _rig.Sessions.TypeAsync(id, name, "阿華", replace: true, submit: false, default);
        Assert.Equal("阿華", await ValueAsync(state, "input[aria-label=姓名]"));
    }

    [RealBrowserFact]
    public async Task Type_into_password_field_throws_and_types_nothing()
    {
        var (id, state) = await OpenAsync("/");

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.TypeAsync(id, RefOf(state, "密碼", "textbox"), "p@ssw0rd", replace: true, submit: false, default));

        Assert.Equal(BrowserFailureKind.PasswordField, ex.Kind);
        Assert.Contains("browser_wait_for_user", ex.Message);
        Assert.Equal("", await ValueAsync(state, "input[type=password]"));
    }

    [RealBrowserFact]
    public async Task Type_into_autocomplete_password_field_throws()
    {
        var (id, state) = await OpenAsync("/");
        await InjectAsync(state, "<input type=\"text\" autocomplete=\"new-password\" aria-label=\"新密碼欄\"><input type=\"text\" autocomplete=\"current-password\" aria-label=\"舊密碼欄\">");

        foreach (var name in new[] { "新密碼欄", "舊密碼欄" })
        {
            var ex = await Assert.ThrowsAsync<BrowserException>(() =>
                _rig.Sessions.TypeAsync(id, RefOf(state, name), "secret", replace: true, submit: false, default));
            Assert.Equal(BrowserFailureKind.PasswordField, ex.Kind);
        }
    }

    [RealBrowserFact]
    public async Task Type_text_over_limit_throws_TooLarge()
    {
        var (id, state) = await OpenAsync("/");

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.TypeAsync(id, RefOf(state, "姓名", "textbox"), new string('a', 10_001), replace: true, submit: false, default));

        Assert.Equal(BrowserFailureKind.TooLarge, ex.Kind);
    }

    [RealBrowserFact]
    public async Task Type_with_submit_submits_the_form()
    {
        var (id, state) = await OpenAsync("/");
        await InjectAsync(state,
            "<form onsubmit=\"event.preventDefault(); document.title = 'submitted:' + this.q.value\"><input name=\"q\" aria-label=\"搜尋\"></form>");

        var outcome = await _rig.Sessions.TypeAsync(id, RefOf(state, "搜尋", "textbox"), "咖啡", replace: true, submit: true, default);

        Assert.Equal("submitted:咖啡", await state.Page.TitleAsync());
        Assert.Equal("submitted:咖啡", outcome.Snapshot.Title);
    }

    [RealBrowserFact]
    public async Task Type_message_and_logs_never_contain_the_typed_text()
    {
        var logger = new CollectingLogger();
        using var site = new LocalSite();
        var options = new BrowserOptions { ProfileRoot = Path.Combine(Path.GetTempPath(), "lumi-browser-act-" + Guid.NewGuid().ToString("N")) };
        await using var sessions = new PlaywrightBrowserSessions(
            TestBrowsers.Instance, FakeCatalog.Create(AppSettings.Empty), new NoSecrets(), options, TimeProvider.System, logger);
        try
        {
            var (id, state) = await OpenAsync(sessions, site.Url("/"));
            const string secret = "絕密內容-7f3a9c";

            var outcome = await sessions.TypeAsync(id, RefOf(state, "姓名", "textbox"), secret, replace: true, submit: false, default);
            Assert.DoesNotContain(secret, outcome.Message);

            // 對按鈕做 Fill 會丟 PlaywrightException，逼出「失敗」的 log 路徑
            var ex = await Assert.ThrowsAsync<BrowserException>(() =>
                sessions.TypeAsync(id, RefOf(state, "送出", "button"), secret, replace: true, submit: false, default));
            Assert.DoesNotContain(secret, ex.Message);

            Assert.DoesNotContain(logger.Lines, line => line.Contains(secret, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(options.ProfileRoot))
            {
                try
                {
                    Directory.Delete(options.ProfileRoot, recursive: true);
                }
                catch (IOException)
                {
                    // 暫存資料夾刪不掉不影響測試結果
                }
            }
        }
    }

    // ---- 選擇 ----

    [RealBrowserFact]
    public async Task Select_matches_by_value_and_by_label()
    {
        var (id, state) = await OpenAsync("/form");
        var city = RefOf(state, "城市");

        var byValue = await _rig.Sessions.SelectAsync(id, city, ["khh"], default);
        Assert.Equal("khh", await state.Page.Locator("select").InputValueAsync());
        Assert.Contains("已選擇 1 個選項", byValue.Message);

        city = RefOf(state, "城市");
        await _rig.Sessions.SelectAsync(id, city, ["台北"], default);
        Assert.Equal("tpe", await state.Page.Locator("select").InputValueAsync());

        // 送出表單確認值真的被頁面看到
        await _rig.Sessions.SelectAsync(id, RefOf(state, "城市"), ["高雄"], default);
        await _rig.Sessions.ClickAsync(id, RefOf(state, "送出表單", "button"), default);
        Assert.Equal("送出：khh||", await state.Page.Locator("#result").InnerTextAsync());
    }

    [RealBrowserFact]
    public async Task Select_with_no_values_or_too_many_throws_Other()
    {
        var (id, state) = await OpenAsync("/form");
        var city = RefOf(state, "城市");

        var none = await Assert.ThrowsAsync<BrowserException>(() => _rig.Sessions.SelectAsync(id, city, [], default));
        Assert.Equal(BrowserFailureKind.Other, none.Kind);
        Assert.Contains("1 到 20", none.Message);

        var many = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.SelectAsync(id, city, Enumerable.Range(0, 21).Select(i => "v" + i).ToArray(), default));
        Assert.Equal(BrowserFailureKind.Other, many.Kind);
    }

    // ---- 按鍵 ----

    [RealBrowserFact]
    public async Task Press_rejects_keys_outside_the_allow_list()
    {
        var (id, _) = await OpenAsync("/");

        var ex = await Assert.ThrowsAsync<BrowserException>(() => _rig.Sessions.PressAsync(id, "F5", default));

        Assert.Equal(BrowserFailureKind.Other, ex.Kind);
        Assert.Contains("F5", ex.Message);
    }

    [RealBrowserFact]
    public async Task Press_sends_key_to_focused_element()
    {
        var (id, state) = await OpenAsync("/");
        await InjectAsync(state, "<input aria-label=\"鍵盤\" onkeydown=\"document.title = 'key:' + event.key\">");
        await state.Page.Locator("input[aria-label=鍵盤]").FocusAsync();

        var outcome = await _rig.Sessions.PressAsync(id, "escape", default);

        Assert.Equal("key:Escape", await state.Page.TitleAsync());
        Assert.Contains("已按下 Escape", outcome.Message);
    }

    [RealBrowserFact]
    public async Task Press_accepts_every_allowed_key()
    {
        var (id, _) = await OpenAsync("/");

        foreach (var key in BrowserRules.AllowedKeys)
            await _rig.Sessions.PressAsync(id, key, default);
    }

    // ---- 上傳 ----

    [RealBrowserFact]
    public async Task Upload_to_file_input_sets_the_file()
    {
        var (id, state) = await OpenAsync("/form");
        var path = _rig.WriteFile("report.txt", "內容");

        var outcome = await _rig.Sessions.UploadAsync(id, RefOf(state, "附件"), path, default);

        Assert.Contains("report.txt", outcome.Message);
        Assert.DoesNotContain(_rig.Directory, outcome.Message);
        await _rig.Sessions.ClickAsync(id, RefOf(state, "送出表單", "button"), default);
        Assert.Equal("送出：tpe||report.txt", await state.Page.Locator("#result").InnerTextAsync());
    }

    [RealBrowserFact]
    public async Task Upload_via_button_that_opens_a_file_chooser()
    {
        var (id, state) = await OpenAsync("/form");
        await InjectAsync(state,
            "<input type=\"file\" id=\"hidden-file\" style=\"display:none\" onchange=\"document.title = 'picked:' + this.files[0].name\">"
            + "<button onclick=\"document.getElementById('hidden-file').click()\">選擇檔案</button>");
        var path = _rig.WriteFile("via-button.txt", "x");

        var outcome = await _rig.Sessions.UploadAsync(id, RefOf(state, "選擇檔案", "button"), path, default);

        Assert.Contains("via-button.txt", outcome.Message);
        Assert.Equal("picked:via-button.txt", await state.Page.TitleAsync());
    }

    [RealBrowserFact]
    public async Task Upload_missing_file_throws_Other()
    {
        var (id, state) = await OpenAsync("/form");

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.UploadAsync(id, RefOf(state, "附件"), Path.Combine(_rig.Directory, "nope.txt"), default));

        Assert.Equal(BrowserFailureKind.Other, ex.Kind);
        Assert.Contains("找不到要上傳的檔案", ex.Message);
    }

    [RealBrowserFact]
    public async Task Upload_with_stale_ref_throws_StaleRef()
    {
        var (id, _) = await OpenAsync("/form");
        var path = _rig.WriteFile("stale.txt", "x");

        var ex = await Assert.ThrowsAsync<BrowserException>(() => _rig.Sessions.UploadAsync(id, "e9999", path, default));

        Assert.Equal(BrowserFailureKind.StaleRef, ex.Kind);
    }

    // ---- 假物件 ----

    private sealed class CollectingLogger : ILogger<PlaywrightBrowserSessions>
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue(formatter(state, exception));
            if (exception is not null)
                _lines.Enqueue(exception.ToString());
        }
    }

    private sealed class NoSecrets : ISecretStore
    {
        public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<SecretValue?>(null);

        public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);
    }
}
