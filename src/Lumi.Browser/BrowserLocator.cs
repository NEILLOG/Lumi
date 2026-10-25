using System.Diagnostics;
using System.Text.RegularExpressions;
using Lumi.Core.Browser;

namespace Lumi.Browser;

internal enum BrowserPlatform { Windows, MacOS, Other }

/// <summary>偵測電腦上已安裝的 Edge 與 Chrome（D1）：只看固定的安裝位置，不啟動瀏覽器。</summary>
public sealed partial class BrowserLocator : IBrowserLocator
{
    private readonly BrowserPlatform _platform;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly string _homeDirectory;
    private readonly Func<string, string?> _readVersion;

    public BrowserLocator()
        : this(
            OperatingSystem.IsWindows() ? BrowserPlatform.Windows
                : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS
                : BrowserPlatform.Other,
            File.Exists,
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            path => ReadVersion(path, OperatingSystem.IsWindows() ? BrowserPlatform.Windows
                : OperatingSystem.IsMacOS() ? BrowserPlatform.MacOS
                : BrowserPlatform.Other))
    {
    }

    internal BrowserLocator(
        BrowserPlatform platform, Func<string, bool> fileExists, Func<string, string?> getEnvironmentVariable,
        string homeDirectory, Func<string, string?> readVersion)
    {
        _platform = platform;
        _fileExists = fileExists;
        _getEnvironmentVariable = getEnvironmentVariable;
        _homeDirectory = homeDirectory;
        _readVersion = readVersion;
    }

    public IReadOnlyList<InstalledBrowser> Detect()
    {
        var found = new List<InstalledBrowser>();
        foreach (var kind in new[] { BrowserKind.Edge, BrowserKind.Chrome })
        {
            var path = Candidates(kind).FirstOrDefault(_fileExists);
            if (path is not null)
                found.Add(new InstalledBrowser(kind, path, SafeReadVersion(path)));
        }

        return found;
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
        if (selected is not null)
            return new BrowserAvailability(selected, installed, null);

        var problem = choice switch
        {
            BrowserChoice.Edge => "找不到 Microsoft Edge，請先安裝，或到設定改用其他瀏覽器。",
            BrowserChoice.Chrome => "找不到 Google Chrome，請先安裝，或到設定改用其他瀏覽器。",
            _ => "找不到 Microsoft Edge 或 Google Chrome，請先安裝 Microsoft Edge。",
        };
        return new BrowserAvailability(null, installed, problem);
    }

    private IEnumerable<string> Candidates(BrowserKind kind)
    {
        switch (_platform)
        {
            case BrowserPlatform.Windows:
                var relative = kind == BrowserKind.Edge
                    ? Path.Combine("Microsoft", "Edge", "Application", "msedge.exe")
                    : Path.Combine("Google", "Chrome", "Application", "chrome.exe");
                foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "LocalAppData" })
                {
                    var root = _getEnvironmentVariable(variable);
                    if (!string.IsNullOrWhiteSpace(root))
                        yield return Path.Combine(root, relative);
                }

                break;
            case BrowserPlatform.MacOS:
                var app = kind == BrowserKind.Edge
                    ? "Microsoft Edge.app/Contents/MacOS/Microsoft Edge"
                    : "Google Chrome.app/Contents/MacOS/Google Chrome";
                yield return "/Applications/" + app;
                yield return _homeDirectory.TrimEnd('/') + "/Applications/" + app;
                break;
        }
    }

    private string? SafeReadVersion(string path)
    {
        try
        {
            var version = _readVersion(path);
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>預設的版本讀取：Windows 讀檔案版本資訊；macOS 讀 .app 的 Info.plist。任何例外或讀不到回 null。</summary>
    internal static string? ReadVersion(string path, BrowserPlatform platform)
    {
        try
        {
            switch (platform)
            {
                case BrowserPlatform.Windows:
                    return FileVersionInfo.GetVersionInfo(path).FileVersion;
                case BrowserPlatform.MacOS:
                    var app = FindAppFolder(path);
                    if (app is null)
                        return null;
                    var plist = Path.Combine(app, "Contents", "Info.plist");
                    return File.Exists(plist) ? ParsePlistVersion(File.ReadAllText(plist)) : null;
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>從 plist 文字取出 <c>CFBundleShortVersionString</c>；找不到回 null。</summary>
    internal static string? ParsePlistVersion(string plistText)
    {
        var match = ShortVersionRegex().Match(plistText);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? FindAppFolder(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>")]
    private static partial Regex ShortVersionRegex();
}
