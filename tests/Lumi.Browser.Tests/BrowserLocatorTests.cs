using Lumi.Core.Browser;

namespace Lumi.Browser.Tests;

public class BrowserLocatorTests
{
    private const string Home = "/Users/tester";

    private static readonly Dictionary<string, string> WindowsEnv = new()
    {
        ["ProgramFiles(x86)"] = @"C:\PF86",
        ["ProgramFiles"] = @"C:\PF",
        ["LocalAppData"] = @"C:\Local",
    };

    private static string Edge(string root) => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");

    private static string Chrome(string root) => Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");

    private static BrowserLocator Make(
        BrowserPlatform platform, IEnumerable<string> existing, Dictionary<string, string>? env = null,
        Func<string, string?>? readVersion = null)
    {
        var files = new HashSet<string>(existing);
        env ??= WindowsEnv;
        return new BrowserLocator(
            platform, files.Contains, name => env.GetValueOrDefault(name), Home, readVersion ?? (_ => null));
    }

    [Fact]
    public void Windows_picks_first_existing_root_in_order()
    {
        var locator = Make(BrowserPlatform.Windows, [Edge(@"C:\PF"), Edge(@"C:\PF86"), Edge(@"C:\Local")]);
        Assert.Equal(Edge(@"C:\PF86"), Assert.Single(locator.Detect()).Path);

        locator = Make(BrowserPlatform.Windows, [Edge(@"C:\Local"), Edge(@"C:\PF")]);
        Assert.Equal(Edge(@"C:\PF"), Assert.Single(locator.Detect()).Path);

        locator = Make(BrowserPlatform.Windows, [Edge(@"C:\Local")]);
        Assert.Equal(Edge(@"C:\Local"), Assert.Single(locator.Detect()).Path);
    }

    [Fact]
    public void Windows_chrome_uses_same_three_roots_in_order()
    {
        var locator = Make(BrowserPlatform.Windows, [Chrome(@"C:\Local"), Chrome(@"C:\PF")]);
        var chrome = Assert.Single(locator.Detect());
        Assert.Equal(BrowserKind.Chrome, chrome.Kind);
        Assert.Equal(Chrome(@"C:\PF"), chrome.Path);

        locator = Make(BrowserPlatform.Windows, [Chrome(@"C:\Local")]);
        Assert.Equal(Chrome(@"C:\Local"), Assert.Single(locator.Detect()).Path);
    }

    [Fact]
    public void Windows_skips_missing_environment_variables()
    {
        var env = new Dictionary<string, string> { ["ProgramFiles"] = @"C:\PF", ["LocalAppData"] = "  " };
        var existing = new[] { Edge(@"C:\PF"), Edge(@"C:\Local"), Edge("") };
        var locator = Make(BrowserPlatform.Windows, existing, env);
        Assert.Equal(Edge(@"C:\PF"), Assert.Single(locator.Detect()).Path);

        var none = Make(BrowserPlatform.Windows, [Edge("")], new Dictionary<string, string>());
        Assert.Empty(none.Detect());
    }

    [Fact]
    public void Detect_lists_edge_before_chrome()
    {
        var locator = Make(BrowserPlatform.Windows, [Chrome(@"C:\PF"), Edge(@"C:\PF")]);
        var installed = locator.Detect();
        Assert.Equal([BrowserKind.Edge, BrowserKind.Chrome], installed.Select(b => b.Kind));
    }

    [Fact]
    public void Auto_prefers_edge_when_both_exist()
    {
        var locator = Make(BrowserPlatform.Windows, [Chrome(@"C:\PF"), Edge(@"C:\PF")]);
        var result = locator.Resolve(BrowserChoice.Auto);
        Assert.True(result.IsAvailable);
        Assert.Equal(BrowserKind.Edge, result.Selected!.Kind);
        Assert.Null(result.Problem);
        Assert.Equal(2, result.Installed.Count);
    }

    [Fact]
    public void Auto_falls_back_to_chrome()
    {
        var locator = Make(BrowserPlatform.Windows, [Chrome(@"C:\PF")]);
        var result = locator.Resolve(BrowserChoice.Auto);
        Assert.Equal(BrowserKind.Chrome, result.Selected!.Kind);
        Assert.Null(result.Problem);
        Assert.Single(result.Installed);
    }

    [Fact]
    public void Auto_with_nothing_explains()
    {
        var result = Make(BrowserPlatform.Windows, []).Resolve(BrowserChoice.Auto);
        Assert.False(result.IsAvailable);
        Assert.Null(result.Selected);
        Assert.Empty(result.Installed);
        Assert.Equal("找不到 Microsoft Edge 或 Google Chrome，請先安裝 Microsoft Edge。", result.Problem);
    }

    [Fact]
    public void Explicit_edge_missing_explains_even_when_chrome_exists()
    {
        var result = Make(BrowserPlatform.Windows, [Chrome(@"C:\PF")]).Resolve(BrowserChoice.Edge);
        Assert.Null(result.Selected);
        Assert.Equal("找不到 Microsoft Edge，請先安裝，或到設定改用其他瀏覽器。", result.Problem);
        Assert.Equal(BrowserKind.Chrome, Assert.Single(result.Installed).Kind);
    }

    [Fact]
    public void Explicit_chrome_missing_explains_even_when_edge_exists()
    {
        var result = Make(BrowserPlatform.Windows, [Edge(@"C:\PF")]).Resolve(BrowserChoice.Chrome);
        Assert.Null(result.Selected);
        Assert.Equal("找不到 Google Chrome，請先安裝，或到設定改用其他瀏覽器。", result.Problem);
        Assert.Equal(BrowserKind.Edge, Assert.Single(result.Installed).Kind);
    }

    [Fact]
    public void Explicit_choice_selects_that_browser()
    {
        var locator = Make(BrowserPlatform.Windows, [Chrome(@"C:\PF"), Edge(@"C:\PF")]);
        Assert.Equal(BrowserKind.Chrome, locator.Resolve(BrowserChoice.Chrome).Selected!.Kind);
        Assert.Equal(BrowserKind.Edge, locator.Resolve(BrowserChoice.Edge).Selected!.Kind);
    }

    [Fact]
    public void MacOS_checks_system_applications_first()
    {
        const string edge = "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        const string chrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        var installed = Make(BrowserPlatform.MacOS, [edge, chrome, Home + "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"]).Detect();
        Assert.Equal([edge, chrome], installed.Select(b => b.Path));
    }

    [Fact]
    public void MacOS_falls_back_to_home_applications()
    {
        const string edge = Home + "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        const string chrome = Home + "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        var installed = Make(BrowserPlatform.MacOS, [edge, chrome]).Detect();
        Assert.Equal([edge, chrome], installed.Select(b => b.Path));
    }

    [Fact]
    public void Other_platform_finds_nothing()
    {
        var everything = new[] { Edge(@"C:\PF"), "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" };
        var result = Make(BrowserPlatform.Other, everything).Resolve(BrowserChoice.Auto);
        Assert.Empty(result.Installed);
        Assert.False(result.IsAvailable);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void Version_comes_from_read_version_and_failures_become_null()
    {
        var edge = Edge(@"C:\PF");
        var chrome = Chrome(@"C:\PF");
        var locator = Make(BrowserPlatform.Windows, [edge, chrome], readVersion: path =>
            path == edge ? "130.0.1" : throw new IOException("denied"));
        var installed = locator.Detect();
        Assert.Equal("130.0.1", installed[0].Version);
        Assert.Null(installed[1].Version);
    }

    [Fact]
    public void Blank_version_becomes_null()
    {
        var locator = Make(BrowserPlatform.Windows, [Edge(@"C:\PF")], readVersion: _ => "  ");
        Assert.Null(Assert.Single(locator.Detect()).Version);
    }

    private const string SamplePlist = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>CFBundleName</key>
        	<string>Google Chrome</string>
        	<key>CFBundleShortVersionString</key>
        	<string>130.0.6723.92</string>
        	<key>CFBundleVersion</key>
        	<string>6723.92</string>
        </dict>
        </plist>
        """;

    [Fact]
    public void Plist_version_is_parsed()
    {
        Assert.Equal("130.0.6723.92", BrowserLocator.ParsePlistVersion(SamplePlist));
        Assert.Null(BrowserLocator.ParsePlistVersion("<plist><dict></dict></plist>"));
        Assert.Null(BrowserLocator.ParsePlistVersion(""));
    }

    [Fact]
    public void MacOS_default_read_version_reads_info_plist_of_the_app()
    {
        var root = Path.Combine(Path.GetTempPath(), "lumi-locator-" + Guid.NewGuid().ToString("N"));
        try
        {
            var contents = Path.Combine(root, "Google Chrome.app", "Contents");
            Directory.CreateDirectory(Path.Combine(contents, "MacOS"));
            File.WriteAllText(Path.Combine(contents, "Info.plist"), SamplePlist);
            var exe = Path.Combine(contents, "MacOS", "Google Chrome");

            Assert.Equal("130.0.6723.92", BrowserLocator.ReadVersion(exe, BrowserPlatform.MacOS));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MacOS_default_read_version_returns_null_when_unreadable()
    {
        Assert.Null(BrowserLocator.ReadVersion("/nonexistent/Foo.app/Contents/MacOS/Foo", BrowserPlatform.MacOS));
        Assert.Null(BrowserLocator.ReadVersion("/nonexistent/no-app-folder/foo", BrowserPlatform.MacOS));
        Assert.Null(BrowserLocator.ReadVersion("/anything", BrowserPlatform.Other));
    }

    [Fact]
    public void Public_constructor_can_detect_without_throwing()
    {
        var result = new BrowserLocator().Resolve(BrowserChoice.Auto);
        Assert.Equal(result.IsAvailable, result.Problem is null);
    }
}
