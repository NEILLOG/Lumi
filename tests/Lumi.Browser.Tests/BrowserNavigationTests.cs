using System.Reflection;
using System.Text.RegularExpressions;
using Lumi.Browser.Tests.TestSupport;
using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Web;
using Microsoft.Playwright;
using Xunit;

namespace Lumi.Browser.Tests;

/// <summary>導覽與讀取（18D）。全部要真瀏覽器：<c>LUMI_BROWSER_TESTS=1</c> 且電腦上有 Edge 或 Chrome。</summary>
public sealed class BrowserNavigationTests
{
    private const string Conv = "nav-1";

    private static readonly CancellationToken Ct = CancellationToken.None;

    private static BrowserPageState StateOf(BrowserTestRig rig, string conversationId)
    {
        var host = (BrowserHost)typeof(PlaywrightBrowserSessions)
            .GetField("_host", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rig.Sessions)!;
        return host.TryGetState(conversationId)!;
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe) where T : class
    {
        for (var i = 0; i < 100; i++)
        {
            if (probe() is { } value)
                return value;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException("等候的條件沒有成立。");
    }

    [RealBrowserFact]
    public async Task Open_returns_snapshot_with_refs_iframe_refs_and_absolute_links()
    {
        using var rig = new BrowserTestRig();

        var outcome = await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);

        Assert.Contains("已開啟 127.0.0.1/。", outcome.Message);
        Assert.Equal("POC", outcome.Snapshot.Title);
        Assert.Contains("button \"送出\"", outcome.Snapshot.Text);
        Assert.Contains("[ref=", outcome.Snapshot.Text);
        Assert.Matches(@"\[ref=f\d+e\d+\]", outcome.Snapshot.Text);
        Assert.Contains("框內按鈕", outcome.Snapshot.Text);
        Assert.Contains($"/url: {rig.Url("/whoami").AbsoluteUri}", outcome.Snapshot.Text);
        Assert.True(outcome.Snapshot.RefCount > 0);
    }

    [RealBrowserFact]
    public async Task Open_reports_http_error_status()
    {
        using var rig = new BrowserTestRig();

        var outcome = await rig.Sessions.OpenAsync(Conv, rig.Url("/no-such-page"), Ct);

        Assert.Contains("（伺服器回應 404）", outcome.Message);
    }

    [RealBrowserFact]
    public async Task Open_unreachable_host_throws_Navigation_with_error_code()
    {
        using var rig = new BrowserTestRig();

        var ex = await Assert.ThrowsAsync<BrowserException>(
            () => rig.Sessions.OpenAsync(Conv, new Uri("http://127.0.0.1:1/"), Ct));

        Assert.Equal(BrowserFailureKind.Navigation, ex.Kind);
        Assert.Contains("net::ERR_", ex.Message);
        Assert.DoesNotContain("127.0.0.1", ex.Message);
    }

    [RealBrowserFact]
    public async Task Open_slow_page_throws_Timeout_when_network_timeout_is_one_second()
    {
        using var rig = new BrowserTestRig(AppSettings.Empty with { Network = new NetworkSettings { TimeoutSeconds = 1 } });

        var ex = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.OpenAsync(Conv, rig.Url("/slow"), Ct));

        Assert.Equal(BrowserFailureKind.Timeout, ex.Kind);
        Assert.Equal("操作逾時。", ex.Message);
    }

    [RealBrowserFact]
    public async Task Open_rejects_non_http_scheme()
    {
        using var rig = new BrowserTestRig();

        var ex = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.OpenAsync(Conv, new Uri("file:///etc/hosts"), Ct));

        Assert.Equal(BrowserFailureKind.Other, ex.Kind);
    }

    [RealBrowserFact]
    public async Task Snapshot_requires_an_open_page_and_respects_max_chars()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync("other", rig.Url("/"), Ct);
        var missing = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.SnapshotAsync(Conv, null, Ct));
        Assert.Equal(BrowserFailureKind.NoPage, missing.Kind);

        await rig.Sessions.OpenAsync(Conv, rig.Url("/tall"), Ct);
        var snapshot = await rig.Sessions.SnapshotAsync(Conv, 100, Ct);

        Assert.Contains("很長的頁面", snapshot.Text);
        Assert.Contains("底部連結", snapshot.Text);
    }

    [RealBrowserFact]
    public async Task GetContent_returns_whole_page_html_title_and_url()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);

        var content = await rig.Sessions.GetContentAsync(Conv, null, Ct);

        Assert.Equal(rig.Url("/"), content.Url);
        Assert.Equal("POC", content.Title);
        Assert.Contains("<h1>測試頁</h1>", content.Html);
        Assert.Null(content.Text);
    }

    [RealBrowserFact]
    public async Task GetContent_for_a_ref_returns_element_html_and_text()
    {
        using var rig = new BrowserTestRig();
        var outcome = await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);
        var heading = Regex.Match(outcome.Snapshot.Text, @"heading ""測試頁""[^\n]*\[ref=(e\d+)\]").Groups[1].Value;
        Assert.NotEmpty(heading);

        var content = await rig.Sessions.GetContentAsync(Conv, heading, Ct);

        Assert.StartsWith("<h1", content.Html);
        Assert.Equal("測試頁", content.Text);
    }

    [RealBrowserFact]
    public async Task Missing_ref_throws_StaleRef_for_content_tables_and_scroll()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);

        foreach (var bad in new[] { "e999", "f9e9", "not a ref", "e1 >> text=x" })
        {
            var a = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.GetContentAsync(Conv, bad, Ct));
            var b = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.GetTablesAsync(Conv, bad, Ct));
            var c = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Down, bad, Ct));
            Assert.All(new[] { a, b, c }, ex => Assert.Equal(BrowserFailureKind.StaleRef, ex.Kind));
            Assert.Contains("請先 browser_snapshot", a.Message);
        }
    }

    [RealBrowserFact]
    public async Task GetTables_reads_two_rows_of_two_cells()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);

        var tables = await rig.Sessions.GetTablesAsync(Conv, null, Ct);

        var table = Assert.Single(tables);
        Assert.Null(table.Caption);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["a1", "b1"], table.Rows[0]);
        Assert.Equal(["a2", "b2"], table.Rows[1]);
    }

    [RealBrowserFact]
    public async Task GetTables_for_a_table_ref_includes_the_table_itself()
    {
        using var rig = new BrowserTestRig();
        var outcome = await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);
        var table = Regex.Match(outcome.Snapshot.Text, @"table[^\n]*\[ref=(e\d+)\]").Groups[1].Value;
        Assert.NotEmpty(table);

        var tables = await rig.Sessions.GetTablesAsync(Conv, table, Ct);

        Assert.Single(tables);
        Assert.Equal(2, tables[0].Rows.Count);
    }

    [RealBrowserFact]
    public async Task GetTables_on_a_page_without_tables_returns_empty()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/form"), Ct);

        Assert.Empty(await rig.Sessions.GetTablesAsync(Conv, null, Ct));
    }

    [RealBrowserFact]
    public async Task Screenshot_is_a_jpeg_within_the_size_limit()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);

        var shot = await rig.Sessions.ScreenshotAsync(Conv, fullPage: false, Ct);

        Assert.Equal("image/jpeg", shot.MediaType);
        Assert.Equal(0xFF, shot.Data[0]);
        Assert.Equal(0xD8, shot.Data[1]);
        Assert.True(shot.Data.Length <= BrowserLimits.MaxScreenshotBytes);
        Assert.Equal(1280, shot.Width);
        Assert.Equal(800, shot.Height);
    }

    [RealBrowserFact]
    public async Task Full_page_screenshot_covers_the_tall_page()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/tall"), Ct);

        var viewport = await rig.Sessions.ScreenshotAsync(Conv, fullPage: false, Ct);
        var full = await rig.Sessions.ScreenshotAsync(Conv, fullPage: true, Ct);

        Assert.Equal(0xFF, full.Data[0]);
        Assert.Equal(0xD8, full.Data[1]);
        Assert.InRange(full.Height, 3000, BrowserLimits.FullPageMaxHeight);
        Assert.True(full.Data.Length > 0 && full.Data.Length <= BrowserLimits.MaxScreenshotBytes);
        Assert.Equal(800, viewport.Height);
    }

    [RealBrowserFact]
    public async Task Full_page_screenshot_is_clipped_to_the_height_limit()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/tall"), Ct);
        await StateOf(rig, Conv).Page.EvaluateAsync("document.body.style.minHeight = '9000px'");

        var full = await rig.Sessions.ScreenshotAsync(Conv, fullPage: true, Ct);

        Assert.Equal(BrowserLimits.FullPageMaxHeight, full.Height);
        Assert.Equal(1280, full.Width);
        Assert.Equal(0xFF, full.Data[0]);
    }

    [RealBrowserFact]
    public async Task Scroll_down_moves_the_page_and_top_returns()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/tall"), Ct);
        var page = StateOf(rig, Conv).Page;
        Assert.Equal(0, await page.EvaluateAsync<int>("Math.round(window.scrollY)"));

        var down = await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Down, null, Ct);
        Assert.Contains("已往下捲動", down.Message);
        Assert.True(await page.EvaluateAsync<int>("Math.round(window.scrollY)") > 0);

        var bottom = await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Bottom, null, Ct);
        Assert.Contains("底部連結", bottom.Snapshot.Text);

        var up = await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Up, null, Ct);
        Assert.Contains("已往上捲動", up.Message);

        await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Top, null, Ct);
        Assert.Equal(0, await page.EvaluateAsync<int>("Math.round(window.scrollY)"));
    }

    [RealBrowserFact]
    public async Task Scroll_to_a_ref_brings_it_into_view()
    {
        using var rig = new BrowserTestRig();
        var opened = await rig.Sessions.OpenAsync(Conv, rig.Url("/tall"), Ct);
        var link = Regex.Match(opened.Snapshot.Text, @"link ""底部連結""[^\n]*\[ref=(e\d+)\]").Groups[1].Value;
        Assert.NotEmpty(link);

        var outcome = await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Down, link, Ct);

        Assert.Contains("link \"底部連結\"", outcome.Message);
        Assert.True(await StateOf(rig, Conv).Page.EvaluateAsync<int>("Math.round(window.scrollY)") > 1000);
    }

    [RealBrowserFact]
    public async Task Back_returns_to_the_previous_page_and_fails_when_there_is_none()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);
        await rig.Sessions.OpenAsync(Conv, rig.Url("/form"), Ct);

        var back = await rig.Sessions.BackAsync(Conv, Ct);

        Assert.Contains("已回到上一頁", back.Message);
        Assert.Equal(rig.Url("/"), back.Snapshot.Url);

        // 第一個頁面之前還有 about:blank，所以再退一次會回到空白頁；再退就沒有了。
        await rig.Sessions.BackAsync(Conv, Ct);
        var ex = await Assert.ThrowsAsync<BrowserException>(() => rig.Sessions.BackAsync(Conv, Ct));
        Assert.Equal(BrowserFailureKind.Other, ex.Kind);
        Assert.Equal("沒有上一頁。", ex.Message);
    }

    [RealBrowserFact]
    public async Task New_tab_note_appears_in_the_next_outcome()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);
        var state = StateOf(rig, Conv);
        var original = state.Page;

        await original.EvaluateAsync("window.open('/whoami', '_blank')");
        await WaitForAsync(() => ReferenceEquals(state.PageOrNull, original) ? null : state.PageOrNull);
        var outcome = await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Down, null, Ct);

        Assert.Contains("點擊開啟了新分頁", outcome.Message);
        Assert.Contains("127.0.0.1", outcome.Message);
        Assert.Equal(rig.Url("/whoami"), outcome.Snapshot.Url);
    }

    [RealBrowserFact]
    public async Task Dialog_note_appears_in_the_next_outcome()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);

        await StateOf(rig, Conv).Page.EvaluateAsync("alert('hi')");
        var outcome = await rig.Sessions.ScrollAsync(Conv, BrowserScrollDirection.Down, null, Ct);

        Assert.Contains("頁面跳出對話框「hi」，已取消。", outcome.Message);
    }

    [RealBrowserFact]
    public async Task Cancelled_token_throws_OperationCanceled()
    {
        using var rig = new BrowserTestRig();
        await rig.Sessions.OpenAsync(Conv, rig.Url("/"), Ct);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Sessions.SnapshotAsync(Conv, null, cts.Token));
    }
}
