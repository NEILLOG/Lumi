using Lumi.Core.Browser;
using Xunit;

namespace Lumi.Browser.Tests.TestSupport;

/// <summary>
/// 真的啟動瀏覽器的測試。沒有設環境變數 <c>LUMI_BROWSER_TESTS=1</c>、或電腦上找不到 Edge／Chrome 時自動略過。
/// </summary>
public sealed class RealBrowserFactAttribute : SkippableFactAttribute
{
    public RealBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("LUMI_BROWSER_TESTS") != "1" || TestBrowsers.Instance.Detect().Count == 0)
            Skip = "需要 LUMI_BROWSER_TESTS=1 且電腦上有 Edge 或 Chrome。";
    }
}

/// <summary>
/// 測試用的瀏覽器偵測：只看各平台的標準安裝位置（Edge 優先、其次 Chrome）。
/// 測試不依賴 18B 的 <c>BrowserLocator</c>，這樣 18C 可以獨立驗證。
/// </summary>
internal sealed class TestBrowsers : IBrowserLocator
{
    public static readonly TestBrowsers Instance = new();

    public IReadOnlyList<InstalledBrowser> Detect()
    {
        var list = new List<InstalledBrowser>();
        foreach (var path in Candidates(BrowserKind.Edge).Where(File.Exists).Take(1))
            list.Add(new InstalledBrowser(BrowserKind.Edge, path, null));
        foreach (var path in Candidates(BrowserKind.Chrome).Where(File.Exists).Take(1))
            list.Add(new InstalledBrowser(BrowserKind.Chrome, path, null));
        return list;
    }

    public BrowserAvailability Resolve(BrowserChoice choice)
    {
        var installed = Detect();
        var selected = choice switch
        {
            BrowserChoice.Edge => installed.FirstOrDefault(b => b.Kind == BrowserKind.Edge),
            BrowserChoice.Chrome => installed.FirstOrDefault(b => b.Kind == BrowserKind.Chrome),
            _ => installed.FirstOrDefault(),
        };
        return new BrowserAvailability(selected, installed, selected is null ? "找不到瀏覽器。" : null);
    }

    private static IEnumerable<string> Candidates(BrowserKind kind)
    {
        if (OperatingSystem.IsMacOS())
        {
            yield return kind == BrowserKind.Edge
                ? "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
                : "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(root))
                    continue;
                yield return kind == BrowserKind.Edge
                    ? Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe")
                    : Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            }
        }
        else
        {
            yield return kind == BrowserKind.Edge ? "/usr/bin/microsoft-edge" : "/usr/bin/google-chrome";
        }
    }
}
