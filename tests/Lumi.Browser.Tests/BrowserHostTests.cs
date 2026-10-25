using System.Reflection;
using Lumi.Browser.Tests.TestSupport;
using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Xunit;

namespace Lumi.Browser.Tests;

/// <summary>
/// <see cref="BrowserHost"/> 的測試。不需要真瀏覽器的部分（啟動失敗、沒有瀏覽器、Proxy 不支援）一定會跑；
/// <c>[RealBrowserFact]</c> 需要 <c>LUMI_BROWSER_TESTS=1</c> 與電腦上的 Edge／Chrome。
/// </summary>
public sealed class BrowserHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-browser-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<BrowserHost> _hosts = [];
    private readonly LocalSite _site = new();

    public void Dispose()
    {
        foreach (var host in _hosts)
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _site.Dispose();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 暫存資料夾刪不掉不影響測試結果
        }
    }

    private BrowserHost NewHost(
        IBrowserLocator? locator = null, TimeProvider? time = null, AppSettings? settings = null,
        Func<InstalledBrowser, BrowserSettings, NetworkSettings, string?, string, bool, BrowserLaunchSpec>? planner = null)
    {
        var host = new BrowserHost(
            locator ?? TestBrowsers.Instance, FakeCatalog.Create(settings ?? AppSettings.Empty), new FakeSecretStore(),
            new BrowserOptions { ProfileRoot = _root }, time ?? TimeProvider.System, NullLogger.Instance, planner);
        _hosts.Add(host);
        return host;
    }

    // ---- 不需要真瀏覽器 ----

    [Fact]
    public async Task Missing_executable_becomes_LaunchFailed_with_policy_hint()
    {
        var missing = Path.Combine(_root, "no-such-browser");
        var locator = new FakeLocator(new InstalledBrowser(BrowserKind.Chrome, missing, "1.0"));
        var host = NewHost(locator, planner: (b, _, _, _, root, visible) =>
            new BrowserLaunchSpec(b.Path, Path.Combine(root, "chrome"), !visible, [], null, null));

        var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("c1", create: true, CancellationToken.None));

        Assert.Equal(BrowserFailureKind.LaunchFailed, ex.Kind);
        Assert.Contains("Google Chrome", ex.Message);
        Assert.Contains("公司的瀏覽器原則", ex.Message);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task No_browser_throws_NotAvailable_with_the_problem_text()
    {
        const string problem = "找不到 Microsoft Edge 或 Google Chrome，請先安裝 Microsoft Edge。";
        var host = NewHost(new FakeLocator(problem: problem));

        var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("c1", create: true, CancellationToken.None));

        Assert.Equal(BrowserFailureKind.NotAvailable, ex.Kind);
        Assert.Equal(problem, ex.Message);
    }

    [Fact]
    public async Task Planner_ProxyNotSupported_is_passed_through_unchanged()
    {
        var locator = new FakeLocator(new InstalledBrowser(BrowserKind.Chrome, "/x/chrome", null));
        var host = NewHost(locator, planner: (_, _, _, _, _, _) =>
            throw new BrowserException(BrowserFailureKind.ProxyNotSupported, "瀏覽器不支援 file:// 的 PAC 檔，請改用 http 或 https 開頭的 PAC 網址"));

        var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("c1", create: true, CancellationToken.None));

        Assert.Equal(BrowserFailureKind.ProxyNotSupported, ex.Kind);
    }

    [SkippableFact]
    public async Task File_pac_with_the_real_planner_throws_ProxyNotSupported()
    {
        var settings = AppSettings.Empty with { Network = new NetworkSettings { Mode = ProxyMode.Pac, PacUrl = "file:///tmp/proxy.pac" } };
        var locator = new FakeLocator(new InstalledBrowser(BrowserKind.Chrome, "/x/chrome", null));
        var host = NewHost(locator, settings: settings);

        try
        {
            var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("c1", create: true, CancellationToken.None));
            Assert.Equal(BrowserFailureKind.ProxyNotSupported, ex.Kind);
        }
        catch (Xunit.Sdk.ThrowsException e) when (e.InnerException is NotImplementedException)
        {
            Skip.If(true, "BrowserLaunchPlanner.Plan（18B）還沒有合併。");
        }
    }

    [Fact]
    public async Task Never_started_host_throws_Closed_when_not_creating()
    {
        var host = NewHost(new FakeLocator());

        var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("c1", create: false, CancellationToken.None));

        Assert.Equal(BrowserFailureKind.Closed, ex.Kind);
    }

    // ---- 真的啟動瀏覽器 ----

    [RealBrowserFact]
    public async Task Start_gives_a_page_and_each_conversation_gets_its_own()
    {
        var host = NewHost();

        var a = await host.GetStateAsync("a", true, default);
        var b = await host.GetStateAsync("b", true, default);

        Assert.NotNull(a.Page);
        Assert.NotNull(b.Page);
        Assert.NotSame(a.Page, b.Page);
        Assert.Same(a, await host.GetStateAsync("a", false, default));
        Assert.True(host.IsRunning);
    }

    [RealBrowserFact]
    public async Task Fourth_page_in_one_conversation_closes_the_oldest()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/").AbsoluteUri);
        var first = state.Page;

        for (var i = 0; i < BrowserLimits.MaxPagesPerConversation; i++)
        {
            var before = state.Page;
            await state.Page.EvaluateAsync("window.open('/whoami')");
            await Eventually(() => !ReferenceEquals(state.Page, before) || state.PageOrNull is null);
        }

        await Eventually(() => state.Pages.Count == BrowserLimits.MaxPagesPerConversation && first.IsClosed);
        Assert.Equal(BrowserLimits.MaxPagesPerConversation, state.Pages.Count);
        Assert.True(first.IsClosed);
        Assert.Contains("/whoami", state.Page.Url);
    }

    [RealBrowserFact]
    public async Task Popup_becomes_the_current_page_with_a_note()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/").AbsoluteUri);
        var original = state.Page;

        await original.GetByRole(AriaRole.Link, new() { Name = "新分頁" }).ClickAsync();
        await Eventually(() => !ReferenceEquals(state.Page, original));

        Assert.EndsWith("/whoami", state.Page.Url);
        Assert.Equal(2, state.Pages.Count);
        Assert.Contains("新分頁", state.TakeNotes());
    }

    [RealBrowserFact]
    public async Task Capture_updates_ref_labels_and_returns_the_page_snapshot()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/").AbsoluteUri);

        BrowserSnapshot snapshot;
        try
        {
            snapshot = await state.CaptureAsync(BrowserLimits.SnapshotDefaultChars, default);
        }
        catch (NotImplementedException)
        {
            Skip.If(true, "BrowserSnapshotParser.Parse（18A）還沒有合併。");
            return;
        }

        Assert.Equal("POC", snapshot.Title);
        Assert.Equal(_site.Url("/"), snapshot.Url);
        Assert.Contains("送出", snapshot.Text);
        Assert.NotEmpty(state.RefLabels);
    }

    [RealBrowserFact]
    public async Task Dialog_is_dismissed_and_noted()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/").AbsoluteUri);

        await state.Page.GetByRole(AriaRole.Button, new() { Name = "跳對話框" }).ClickAsync();

        var notes = state.TakeNotes();
        Assert.Contains("對話框「hi」", notes);
        Assert.Contains("已取消", notes);
        Assert.Equal("", state.TakeNotes());
    }

    [RealBrowserFact]
    public async Task CloseConversation_then_NoPage_and_CloseAll_then_Closed()
    {
        var host = NewHost();
        await host.GetStateAsync("a", true, default);
        await host.GetStateAsync("b", true, default);

        await host.CloseConversationAsync("a", default);

        var noPage = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("a", false, default));
        Assert.Equal(BrowserFailureKind.NoPage, noPage.Kind);
        Assert.NotNull(await host.GetStateAsync("b", false, default));

        await host.CloseAllAsync();

        var closed = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("b", false, default));
        Assert.Equal(BrowserFailureKind.Closed, closed.Kind);
        Assert.False(host.IsRunning);
    }

    [RealBrowserFact]
    public async Task Cookie_survives_closing_and_reopening_the_browser()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/login").AbsoluteUri);
        await host.CloseAllAsync();

        var again = await host.GetStateAsync("a", true, default);
        await again.Page.GotoAsync(_site.Url("/whoami").AbsoluteUri);

        Assert.Contains("sid=abc123", await again.Page.Locator("#c").InnerTextAsync());
    }

    [RealBrowserFact]
    public async Task ClearProfile_removes_the_profile_folders_and_the_cookie()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/login").AbsoluteUri);
        Assert.True(Directory.Exists(_root));

        await host.ClearProfileAsync(default);

        Assert.False(Directory.Exists(Path.Combine(_root, "edge")));
        Assert.False(Directory.Exists(Path.Combine(_root, "chrome")));
        var again = await host.GetStateAsync("a", true, default);
        await again.Page.GotoAsync(_site.Url("/whoami").AbsoluteUri);
        Assert.DoesNotContain("sid=abc123", await again.Page.Locator("#c").InnerTextAsync());
    }

    [RealBrowserFact]
    public async Task Total_page_limit_closes_the_least_recently_used_conversation()
    {
        var host = NewHost();
        for (var i = 0; i < BrowserLimits.MaxPagesTotal; i++)
            await host.GetStateAsync("c" + i, true, default);

        await host.GetStateAsync("extra", true, default);

        Assert.Null(host.TryGetState("c0"));
        Assert.NotNull(host.TryGetState("extra"));
        Assert.NotNull(host.TryGetState("c1"));
    }

    [RealBrowserFact]
    public async Task Idle_browser_closes_after_ten_minutes_but_not_while_busy()
    {
        var time = new ManualTimeProvider();
        var host = NewHost(time: time);
        var state = await host.GetStateAsync("a", true, default);

        time.Advance(TimeSpan.FromMinutes(5));
        await host.CheckIdleAsync();
        Assert.True(host.IsRunning);

        time.Advance(TimeSpan.FromMinutes(6));
        await state.Gate.WaitAsync();
        await host.CheckIdleAsync();
        Assert.True(host.IsRunning); // 有操作進行中不關
        state.Gate.Release();

        await host.CheckIdleAsync();

        Assert.False(host.IsRunning);
        var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("a", false, default));
        Assert.Equal(BrowserFailureKind.Closed, ex.Kind);
    }

    [RealBrowserFact]
    public async Task User_closing_the_browser_clears_the_state()
    {
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);

        await state.Page.Context.CloseAsync();
        await Eventually(() => !host.IsRunning);

        var ex = await Assert.ThrowsAsync<BrowserException>(() => host.GetStateAsync("a", false, default));
        Assert.Equal(BrowserFailureKind.Closed, ex.Kind);
        var reopened = await host.GetStateAsync("a", true, default);
        Assert.NotNull(reopened.Page);
    }

    [SkippableFact]
    public async Task EnsureVisible_restarts_with_a_window_and_keeps_cookie_and_url()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("LUMI_BROWSER_TESTS") == "1" && TestBrowsers.Instance.Detect().Count > 0,
            "需要 LUMI_BROWSER_TESTS=1 且電腦上有 Edge 或 Chrome。");
        Skip.IfNot(Environment.GetEnvironmentVariable("LUMI_BROWSER_TESTS_VISIBLE") == "1", "需要 LUMI_BROWSER_TESTS_VISIBLE=1（會跳出瀏覽器視窗）。");
        var host = NewHost();
        var state = await host.GetStateAsync("a", true, default);
        await state.Page.GotoAsync(_site.Url("/login").AbsoluteUri);
        await state.Page.GotoAsync(_site.Url("/whoami").AbsoluteUri);

        await host.EnsureVisibleAsync(default);

        Assert.True(host.IsVisible);
        Assert.Same(state, await host.GetStateAsync("a", false, default));
        Assert.EndsWith("/whoami", state.Page.Url);
        Assert.Contains("sid=abc123", await state.Page.Locator("#c").InnerTextAsync());
        Assert.Contains("重新啟動", state.TakeNotes());
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("等待條件成立逾時。");
            await Task.Delay(50);
        }
    }

    // ---- 替身 ----

    private sealed class FakeLocator : IBrowserLocator
    {
        private readonly IReadOnlyList<InstalledBrowser> _installed;
        private readonly string? _problem;

        public FakeLocator(InstalledBrowser? browser = null, string? problem = null)
        {
            _installed = browser is null ? [] : [browser];
            _problem = problem;
        }

        public IReadOnlyList<InstalledBrowser> Detect() => _installed;

        public BrowserAvailability Resolve(BrowserChoice choice) =>
            new(_installed.FirstOrDefault(), _installed, _installed.Count == 0 ? _problem ?? "找不到瀏覽器。" : null);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<SecretValue?>(null);

        public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);

        public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>只實作 <see cref="IModelCatalog.Current"/> 的假設定來源（其他成員回預設值）；BrowserHost 只讀 Current。</summary>
public class FakeCatalog : DispatchProxy
{
    private AppSettings _settings = AppSettings.Empty;

    public static IModelCatalog Create(AppSettings settings)
    {
        var proxy = Create<IModelCatalog, FakeCatalog>();
        ((FakeCatalog)(object)proxy)._settings = settings;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            return null;
        if (targetMethod.Name == "get_Current")
            return _settings;
        var type = targetMethod.ReturnType;
        if (type == typeof(Task))
            return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}
