using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

// Lumi 瀏覽器 POC（M18）。用法見 README.md：
//   dotnet run -- <edge|chrome|瀏覽器完整路徑> [--visible] [--pac http://…/proxy.pac] [--intranet https://內部網站/]
var arguments = args.ToList();
if (arguments.Count == 0 || arguments[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.WriteLine("用法：dotnet run -- <edge|chrome|瀏覽器完整路徑> [--visible] [--pac http://…/proxy.pac] [--intranet https://內部網站/]");
    return 2;
}

string? Option(string name) { var i = arguments.IndexOf(name); return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null; }
var visible = arguments.Contains("--visible");
var userPac = Option("--pac");
var intranet = Option("--intranet");
var executable = ResolveBrowser(arguments[0]);
if (executable is null || !File.Exists(executable))
{
    Console.WriteLine($"找不到瀏覽器：{arguments[0]}");
    return 2;
}

Console.WriteLine($"系統：{RuntimeInformation.OSDescription}");
Console.WriteLine($"瀏覽器：{executable}");
Console.WriteLine($"模式：{(visible ? "顯示視窗" : "背景")}\n");

var failures = 0;
async Task Check(string name, Func<Task<string?>> body)
{
    try
    {
        var note = await body();
        Console.WriteLine(note is null ? $"PASS  {name}" : $"PASS  {name}（{note}）");
    }
    catch (SkipException e) { Console.WriteLine($"SKIP  {name}：{e.Message}"); }
    catch (Exception e) { failures++; Console.WriteLine($"FAIL  {name}：{e.Message.Split('\n')[0]}"); }
}

var profile = Path.Combine(Path.GetTempPath(), "lumi-browserpoc-" + Guid.NewGuid().ToString("N"));
var tcp = new TcpListener(IPAddress.Loopback, 0); tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
var site = new HttpListener(); site.Prefixes.Add($"http://127.0.0.1:{port}/"); site.Start();
_ = Task.Run(() => ServeAsync(site, port));

using var pw = await Playwright.CreateAsync();
Task<IBrowserContext> Launch(string dir, bool headless, params string[] extraArgs) =>
    pw.Chromium.LaunchPersistentContextAsync(dir, new BrowserTypeLaunchPersistentContextOptions
    {
        ExecutablePath = executable, Headless = headless, Args = extraArgs.Length == 0 ? null : extraArgs,
        ViewportSize = new() { Width = 1280, Height = 800 }, Locale = "zh-TW", AcceptDownloads = true,
    });

IBrowserContext? ctx = null;
IPage? page = null;
var snap = "";

await Check("啟動持久設定檔", async () =>
{
    ctx = await Launch(profile, !visible);
    page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
    return ctx.Browser?.Version is { } v ? $"版本 {v}" : null;
});
if (ctx is null || page is null) { Console.WriteLine("\n瀏覽器無法啟動，後面的項目略過。若公司的瀏覽器原則禁止自動化，請洽 IT。"); return 1; }

await Check("開啟測試頁並取得 AI 快照", async () =>
{
    await page.GotoAsync($"http://127.0.0.1:{port}/");
    snap = await page.AriaSnapshotAsync(new PageAriaSnapshotOptions { Mode = AriaSnapshotMode.Ai });
    if (!Regex.IsMatch(snap, @"\[ref=e\d+\]")) throw new Exception("快照沒有 ref");
    return null;
});
await Check("iframe 內的元素有 ref（fNeM 形式）", () =>
    Regex.IsMatch(snap, @"\[ref=f\d+e\d+\]") ? Task.FromResult<string?>(null) : throw new Exception("找不到"));
await Check("用 aria-ref 點擊按鈕", async () =>
{
    var r = Regex.Match(snap, @"button ""送出""[^\n]*\[ref=(e\d+)\]").Groups[1].Value;
    await page.Locator("aria-ref=" + r).ClickAsync(new() { Timeout = 10000 });
    return await page.TitleAsync() == "clicked" ? null : throw new Exception("標題沒有改變");
});
await Check("用 aria-ref 輸入文字", async () =>
{
    var r = Regex.Match(snap, @"textbox ""姓名""[^\n]*\[ref=(e\d+)\]").Groups[1].Value;
    await page.Locator("aria-ref=" + r).FillAsync("Amy");
    return await page.Locator("aria-ref=" + r).InputValueAsync() == "Amy" ? null : throw new Exception("值不對");
});
await Check("能辨識密碼欄位（type=password）", async () =>
{
    var r = Regex.Match(snap, @"textbox ""密碼""[^\n]*\[ref=(e\d+)\]").Groups[1].Value;
    return await page.Locator("aria-ref=" + r).GetAttributeAsync("type") == "password" ? null : throw new Exception("讀不到 type");
});
await Check("對話框被取消並讀到訊息", async () =>
{
    string? message = null;
    void Handler(object? _, IDialog d) { message = d.Message; _ = d.DismissAsync(); }
    page.Dialog += Handler;
    await page.GetByRole(AriaRole.Button, new() { Name = "跳對話框" }).ClickAsync(new() { Timeout = 10000 });
    page.Dialog -= Handler;
    return message == "hi" ? null : throw new Exception("沒有收到對話框");
});
await Check("新分頁（target=_blank）可接手", async () =>
{
    var popupTask = ctx.WaitForPageAsync();
    await page.GetByRole(AriaRole.Link, new() { Name = "新分頁" }).ClickAsync(new() { Timeout = 10000 });
    var popup = await popupTask; await popup.WaitForLoadStateAsync();
    var ok = popup.Url.EndsWith("/whoami", StringComparison.Ordinal);
    await popup.CloseAsync();
    return ok ? null : throw new Exception("網址不對：" + popup.Url);
});
await Check("截圖（JPEG）", async () =>
{
    var bytes = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 70 });
    return bytes.Length > 1000 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? $"{bytes.Length} 位元組" : throw new Exception("不是 JPEG");
});
await Check("下載檔案", async () =>
{
    var dl = await page.RunAndWaitForDownloadAsync(() => page.GetByRole(AriaRole.Link, new() { Name = "下載" }).ClickAsync(new() { Timeout = 10000 }));
    var dest = Path.Combine(Path.GetTempPath(), "lumi-browserpoc-" + dl.SuggestedFilename);
    await dl.SaveAsAsync(dest);
    return File.ReadAllText(dest) == "hello" ? null : throw new Exception("內容不對");
});

await Check("關閉後重開，登入 Cookie 還在", async () =>
{
    await page.GotoAsync($"http://127.0.0.1:{port}/login");
    await ctx.CloseAsync();
    ctx = await Launch(profile, !visible);
    page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
    await page.GotoAsync($"http://127.0.0.1:{port}/whoami");
    return (await page.Locator("#c").InnerTextAsync()).Contains("sid=abc123") ? null : throw new Exception("Cookie 不見了");
});

await Check("背景 → 顯示視窗 → 背景的重啟，Cookie 還在（D9）", async () =>
{
    if (!visible) throw new SkipException("加上 --visible 才會測（需要能顯示視窗）");
    foreach (var headless in new[] { true, false, true })
    {
        await ctx.CloseAsync();
        ctx = await Launch(profile, headless);
        page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        await page.GotoAsync($"http://127.0.0.1:{port}/whoami");
        if (!(await page.Locator("#c").InnerTextAsync()).Contains("sid=abc123")) throw new Exception($"{(headless ? "背景" : "視窗")}模式下 Cookie 不見了");
    }
    return null;
});
await ctx.CloseAsync();

await Check("--proxy-pac-url（http PAC）會把請求導到指定 Proxy", async () =>
{
    var (line, run) = await ProbeThroughProxy(
        extra => Launch(profile + "-pac", !visible, extra), p => $"--proxy-pac-url=http://127.0.0.1:{port}/pac?p={p}");
    return line.StartsWith("GET http://lumi-pac-test.test/x", StringComparison.Ordinal) ? null : throw new Exception("Proxy 沒有收到請求：" + line);
});

if (userPac is not null)
{
    await Check($"你提供的 PAC（{userPac}）能開 https://example.com", async () =>
    {
        var c = await Launch(profile + "-userpac", !visible, "--proxy-pac-url=" + userPac);
        try
        {
            var p = c.Pages.FirstOrDefault() ?? await c.NewPageAsync();
            var resp = await p.GotoAsync("https://example.com/", new() { Timeout = 30000 });
            return $"HTTP {resp?.Status}，標題「{await p.TitleAsync()}」";
        }
        finally { await c.CloseAsync(); }
    });
}
else Console.WriteLine("SKIP  你提供的 PAC：沒有 --pac 參數");

if (intranet is not null)
{
    await Check($"系統 Proxy／整合驗證能開內部網站（{intranet}）", async () =>
    {
        var c = await Launch(profile + "-intranet", !visible);
        try
        {
            var p = c.Pages.FirstOrDefault() ?? await c.NewPageAsync();
            var resp = await p.GotoAsync(intranet, new() { Timeout = 30000 });
            return resp is { Status: >= 400 } ? throw new Exception($"HTTP {resp.Status}") : $"HTTP {resp?.Status}，標題「{await p.TitleAsync()}」";
        }
        finally { await c.CloseAsync(); }
    });
}
else Console.WriteLine("SKIP  內部網站：沒有 --intranet 參數");

Console.WriteLine($"\n{(failures == 0 ? "全部通過" : $"有 {failures} 項失敗")}。請把上面的輸出貼回來。");
site.Stop();
try { Directory.Delete(profile, true); } catch { }
return failures == 0 ? 0 : 1;

// ---- 輔助 ----

static string? ResolveBrowser(string what)
{
    if (what is not ("edge" or "chrome")) return what;
    var edge = what == "edge";
    var candidates = new List<string>();
    if (OperatingSystem.IsWindows())
    {
        foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles", "LocalAppData" })
            if (Environment.GetEnvironmentVariable(env) is { Length: > 0 } root)
                candidates.Add(Path.Combine(root, edge ? @"Microsoft\Edge\Application\msedge.exe" : @"Google\Chrome\Application\chrome.exe"));
    }
    else if (OperatingSystem.IsMacOS())
    {
        var app = edge ? "Microsoft Edge" : "Google Chrome";
        candidates.Add($"/Applications/{app}.app/Contents/MacOS/{app}");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"Applications/{app}.app/Contents/MacOS/{app}"));
    }
    return candidates.FirstOrDefault(File.Exists);
}

static async Task ServeAsync(HttpListener listener, int port)
{
    while (true)
    {
        var c = await listener.GetContextAsync();
        var path = c.Request.Url!.AbsolutePath;
        string body;
        if (path == "/login") { c.Response.AppendHeader("Set-Cookie", "sid=abc123; Max-Age=3600; Path=/"); body = "<h1>logged in</h1>"; }
        else if (path == "/whoami") body = "<p id=c>" + (c.Request.Headers["Cookie"] ?? "none") + "</p>";
        else if (path == "/pac") body = "function FindProxyForURL(u,h){ if (h=='lumi-pac-test.test') return 'PROXY 127.0.0.1:" + c.Request.QueryString["p"] + "'; return 'DIRECT'; }";
        else if (path == "/dl") { c.Response.AddHeader("Content-Disposition", "attachment; filename=a.txt"); body = "hello"; }
        else body = """
            <html><head><title>POC</title></head><body>
            <h1>測試頁</h1>
            <button onclick="document.title='clicked'">送出</button>
            <input type=text aria-label="姓名" />
            <input type=password aria-label="密碼" />
            <a href="/whoami" target=_blank>新分頁</a>
            <a href="/dl">下載</a>
            <button onclick="alert('hi')">跳對話框</button>
            <iframe srcdoc="<button>框內按鈕</button>"></iframe>
            </body></html>
            """;
        c.Response.ContentType = path == "/pac" ? "application/x-ns-proxy-autoconfig" : "text/html; charset=utf-8";
        await c.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        c.Response.Close();
    }
}

static async Task<(string Line, bool Ok)> ProbeThroughProxy(Func<string, Task<IBrowserContext>> launch, Func<int, string> argFor)
{
    var fake = new TcpListener(IPAddress.Loopback, 0); fake.Start();
    var fakePort = ((IPEndPoint)fake.LocalEndpoint).Port;
    var seen = Task.Run(async () =>
    {
        using var client = await fake.AcceptTcpClientAsync(); using var stream = client.GetStream();
        var buffer = new byte[2048]; var n = await stream.ReadAsync(buffer);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
        return Encoding.ASCII.GetString(buffer, 0, n).Split("\r\n")[0];
    });
    var ctx = await launch(argFor(fakePort));
    try
    {
        var p = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        try { await p.GotoAsync("http://lumi-pac-test.test/x", new() { Timeout = 15000 }); } catch { /* 名稱解析失敗也無妨，只看 Proxy 有沒有收到 */ }
        var done = await Task.WhenAny(seen, Task.Delay(3000));
        return done == seen ? (await seen, true) : ("(沒有收到)", false);
    }
    finally { await ctx.CloseAsync(); fake.Stop(); }
}

sealed class SkipException(string message) : Exception(message);
