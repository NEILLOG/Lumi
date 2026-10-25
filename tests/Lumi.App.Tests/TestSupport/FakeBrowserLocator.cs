using Lumi.Core.Browser;

namespace Lumi.App.Tests.TestSupport;

/// <summary>可以設定「電腦上裝了哪些瀏覽器」的偵測器；<see cref="Resolve"/> 的規則同正式實作（Auto＝Edge 優先，其次 Chrome）。</summary>
internal sealed class FakeBrowserLocator : IBrowserLocator
{
    public FakeBrowserLocator(params InstalledBrowser[] installed)
    {
        Installed = [.. installed];
    }

    /// <summary>目前「裝著」的瀏覽器；測試可以改它，再呼叫「重新偵測」。</summary>
    public List<InstalledBrowser> Installed { get; }

    /// <summary>每次 <see cref="Resolve"/> 收到的選擇（用來確認有沒有重新偵測）。</summary>
    public List<BrowserChoice> ResolveCalls { get; } = [];

    public IReadOnlyList<InstalledBrowser> Detect() => Installed.ToList();

    public BrowserAvailability Resolve(BrowserChoice choice)
    {
        ResolveCalls.Add(choice);
        var installed = Installed.ToList();
        var selected = choice switch
        {
            BrowserChoice.Edge => installed.FirstOrDefault(b => b.Kind == BrowserKind.Edge),
            BrowserChoice.Chrome => installed.FirstOrDefault(b => b.Kind == BrowserKind.Chrome),
            _ => installed.FirstOrDefault(b => b.Kind == BrowserKind.Edge) ?? installed.FirstOrDefault(b => b.Kind == BrowserKind.Chrome),
        };
        var problem = selected is null ? "找不到所選的瀏覽器。" : null;
        return new BrowserAvailability(selected, installed, problem);
    }
}
