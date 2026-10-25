using System.Reflection;
using Lumi.Browser.Tests.TestSupport;
using Lumi.Core.Browser;
using Xunit;

namespace Lumi.Browser.Tests;

/// <summary>
/// 下載與等待使用者（18F）。<c>WaitForUserAsync</c> 會把瀏覽器重啟成可見視窗（D9），
/// 所以真的跑到等待迴圈的測試需要 <c>LUMI_BROWSER_TESTS_VISIBLE=1</c>（視窗會短暫跳出來）。
/// OpenAsync 由 18D 負責，這裡直接用 Playwright 把分頁導向測試頁。
/// </summary>
public sealed class BrowserTransferTests : IDisposable
{
    private const string Conversation = "c1";
    private readonly BrowserTestRig _rig = new();

    public void Dispose() => _rig.Dispose();

    // ---- 輔助 ----

    private static BrowserHost HostOf(PlaywrightBrowserSessions sessions) =>
        (BrowserHost)typeof(PlaywrightBrowserSessions).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sessions)!;

    private async Task<BrowserPageState> GoAsync(string path, string conversation = Conversation)
    {
        var state = await HostOf(_rig.Sessions).GetStateAsync(conversation, true, default);
        await state.Page.GotoAsync(_rig.Url(path).AbsoluteUri);
        await state.CaptureAsync(12000, default);
        return state;
    }

    private static string RefOf(BrowserPageState state, string label)
    {
        var found = state.RefLabels.FirstOrDefault(pair => pair.Value.Contains(label, StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(found.Key), $"快照裡找不到 {label}。");
        return found.Key;
    }

    private string Destination(string name) => Path.Combine(_rig.Directory, name);

    private static void SkipUnlessVisible() =>
        Skip.IfNot(Environment.GetEnvironmentVariable("LUMI_BROWSER_TESTS_VISIBLE") == "1", "需要 LUMI_BROWSER_TESTS_VISIBLE=1（會跳出瀏覽器視窗）。");

    private async Task WaitUntilVisibleAsync()
    {
        var host = HostOf(_rig.Sessions);
        for (var i = 0; i < 200 && !host.IsVisible; i++)
            await Task.Delay(100);
        Assert.True(host.IsVisible);
        await Task.Delay(500);
    }

    // ---- 下載 ----

    [RealBrowserFact]
    public async Task Download_SavesFile()
    {
        var state = await GoAsync("/");
        var destination = Destination("saved.txt");

        var result = await _rig.Sessions.DownloadAsync(Conversation, RefOf(state, "下載"), destination, 1024, default);

        Assert.Equal("a.txt", result.FileName);
        Assert.Equal(5, result.Bytes);
        Assert.Equal("hello", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(destination + ".lumi-download"));
    }

    [RealBrowserFact]
    public async Task Download_ExistingDestination_IsNotOverwritten()
    {
        var state = await GoAsync("/");
        var destination = _rig.WriteFile("exists.txt", "keep");

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.DownloadAsync(Conversation, RefOf(state, "下載"), destination, 1024, default));

        Assert.Equal(BrowserFailureKind.Other, ex.Kind);
        Assert.Contains("沒有覆蓋", ex.Message);
        Assert.Equal("keep", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(destination + ".lumi-download"));
    }

    [RealBrowserFact]
    public async Task Download_OverLimit_ThrowsTooLargeAndLeavesNothing()
    {
        var state = await GoAsync("/");
        var destination = Destination("big.txt");

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.DownloadAsync(Conversation, RefOf(state, "下載"), destination, 3, default));

        Assert.Equal(BrowserFailureKind.TooLarge, ex.Kind);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".lumi-download"));
    }

    [RealBrowserFact]
    public async Task Download_ClickWithoutDownload_ThrowsTimeout()
    {
        var state = await GoAsync("/");
        var destination = Destination("none.txt");
        var original = PlaywrightBrowserSessions.DownloadWaitMilliseconds;
        PlaywrightBrowserSessions.DownloadWaitMilliseconds = 1500;
        try
        {
            var ex = await Assert.ThrowsAsync<BrowserException>(() =>
                _rig.Sessions.DownloadAsync(Conversation, RefOf(state, "送出"), destination, 1024, default));

            Assert.Equal(BrowserFailureKind.Timeout, ex.Kind);
            Assert.Contains("沒有開始下載", ex.Message);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(destination + ".lumi-download"));
        }
        finally
        {
            PlaywrightBrowserSessions.DownloadWaitMilliseconds = original;
        }
    }

    [RealBrowserFact]
    public async Task Download_UnknownRef_ThrowsStaleRef()
    {
        await GoAsync("/");

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.DownloadAsync(Conversation, "e9999", Destination("x.txt"), 1024, default));

        Assert.Equal(BrowserFailureKind.StaleRef, ex.Kind);
    }

    [RealBrowserFact]
    public async Task Download_DoesNotHoldGateAfterFailure()
    {
        var state = await GoAsync("/");

        await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.DownloadAsync(Conversation, "e9999", Destination("x.txt"), 1024, default));

        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Download_BrowserNotStarted_ThrowsClosed()
    {
        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.DownloadAsync(Conversation, "e1", Destination("x.txt"), 1024, default));

        Assert.Equal(BrowserFailureKind.Closed, ex.Kind);
    }

    // ---- 等待使用者：不需要可見視窗的部分 ----

    [Fact]
    public async Task Wait_NoCondition_Throws()
    {
        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.WaitForUserAsync(Conversation, new BrowserWaitCondition(null, " "), TimeSpan.FromSeconds(10), default));

        Assert.Equal(BrowserFailureKind.Other, ex.Kind);
    }

    [Fact]
    public async Task Wait_BrowserNotStarted_ThrowsClosed()
    {
        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.WaitForUserAsync(Conversation, new BrowserWaitCondition("x", null), TimeSpan.FromSeconds(10), default));

        Assert.Equal(BrowserFailureKind.Closed, ex.Kind);
    }

    [RealBrowserFact]
    public async Task Wait_ConversationWithoutPage_ThrowsNoPageWithoutRestartingVisible()
    {
        await GoAsync("/");
        var host = HostOf(_rig.Sessions);

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.WaitForUserAsync("other", new BrowserWaitCondition("x", null), TimeSpan.FromSeconds(10), default));

        Assert.Equal(BrowserFailureKind.NoPage, ex.Kind);
        Assert.False(host.IsVisible);
    }

    // ---- 等待使用者：需要可見視窗 ----

    [RealBrowserFact]
    public async Task Wait_UrlCondition_CompletesWhenPageNavigates()
    {
        SkipUnlessVisible();
        var state = await GoAsync("/");
        var navigate = Task.Run(async () =>
        {
            await WaitUntilVisibleAsync();
            await state.Page.GotoAsync(_rig.Url("/whoami").AbsoluteUri);
        });

        var result = await _rig.Sessions.WaitForUserAsync(
            Conversation, new BrowserWaitCondition("/WHOAMI", null), TimeSpan.FromSeconds(30), default);
        await navigate;

        Assert.True(result.Completed);
        Assert.Contains("已完成", result.Message);
        Assert.EndsWith("/whoami", result.Snapshot.Url.AbsolutePath);
    }

    [RealBrowserFact]
    public async Task Wait_TextCondition_CompletesWhenTextAppears()
    {
        SkipUnlessVisible();
        var state = await GoAsync("/");
        var append = Task.Run(async () =>
        {
            await WaitUntilVisibleAsync();
            await state.Page.EvaluateAsync("() => { const p = document.createElement('p'); p.textContent = '驗證通過'; document.body.append(p); }");
        });

        var result = await _rig.Sessions.WaitForUserAsync(
            Conversation, new BrowserWaitCondition(null, "驗證通過"), TimeSpan.FromSeconds(30), default);
        await append;

        Assert.True(result.Completed);
        Assert.Contains("驗證通過", result.Snapshot.Text);
    }

    [RealBrowserFact]
    public async Task Wait_ConditionNeverMet_ReturnsNotCompleted()
    {
        SkipUnlessVisible();
        await GoAsync("/");

        var result = await _rig.Sessions.WaitForUserAsync(
            Conversation, new BrowserWaitCondition("never-happens", null), TimeSpan.FromSeconds(2), default);

        Assert.False(result.Completed);
        Assert.Contains("等了 2 秒", result.Message);
        Assert.Contains("測試頁", result.Snapshot.Text);
    }

    [RealBrowserFact]
    public async Task Wait_Cancelled_ThrowsOperationCanceled()
    {
        SkipUnlessVisible();
        await GoAsync("/");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(4));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _rig.Sessions.WaitForUserAsync(Conversation, new BrowserWaitCondition("never-happens", null), TimeSpan.FromSeconds(60), cts.Token));
    }

    [RealBrowserFact]
    public async Task Wait_RestartsVisible_KeepsUrlAndAddsNote()
    {
        SkipUnlessVisible();
        await GoAsync("/tall");
        var host = HostOf(_rig.Sessions);
        Assert.False(host.IsVisible);

        var result = await _rig.Sessions.WaitForUserAsync(
            Conversation, new BrowserWaitCondition("never-happens", null), TimeSpan.FromSeconds(2), default);

        Assert.True(host.IsVisible);
        Assert.Equal("/tall", result.Snapshot.Url.AbsolutePath);
        Assert.Contains("重新啟動", result.Message);
    }

    [RealBrowserFact]
    public async Task Wait_WindowClosedByUser_ThrowsClosed()
    {
        SkipUnlessVisible();
        var state = await GoAsync("/");
        var close = Task.Run(async () =>
        {
            await WaitUntilVisibleAsync();
            await state.Page.Context.CloseAsync();
        });

        var ex = await Assert.ThrowsAsync<BrowserException>(() =>
            _rig.Sessions.WaitForUserAsync(Conversation, new BrowserWaitCondition("never-happens", null), TimeSpan.FromSeconds(30), default));
        await close;

        Assert.Equal(BrowserFailureKind.Closed, ex.Kind);
        Assert.Contains("關閉", ex.Message);
    }
}
