using Lumi.Platform.Shell;

namespace Lumi.Platform.Tests;

public sealed class LaunchAtLoginTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-launch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Lumi\Lumi.App.exe", @"C:\Program Files\Lumi\Lumi.App.exe")]
    [InlineData(@"C:\Tools\dotnet.exe", null)]       // dotnet run：不要把開發用的路徑寫進系統
    [InlineData("/usr/local/share/dotnet/dotnet", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void WindowsTarget_RefusesTheDotnetHost(string? processPath, string? expected) =>
        Assert.Equal(expected, LaunchAtLogin.WindowsTarget(processPath));

    [Fact]
    public void WindowsCommand_QuotesThePath() =>
        Assert.Equal("\"C:\\Program Files\\Lumi\\Lumi.App.exe\"", LaunchAtLogin.WindowsCommand(@"C:\Program Files\Lumi\Lumi.App.exe"));

    [Theory]
    [InlineData("/Applications/Lumi.app/Contents/MacOS/Lumi.App", "/Applications/Lumi.app")]
    [InlineData("/Users/me/Apps/Lumi.app/Contents/MacOS/Lumi.App", "/Users/me/Apps/Lumi.app")]
    [InlineData("/Users/me/src/publish/Lumi.App", null)]   // 不在 .app 套件裡
    [InlineData("/usr/local/share/dotnet/dotnet", null)]
    [InlineData(null, null)]
    public void MacBundle_FindsTheEnclosingApp(string? processPath, string? expected) =>
        Assert.Equal(expected, LaunchAtLogin.MacBundle(processPath));

    [Fact]
    public void MacPlist_OpensTheBundleAtLogin_AndEscapesThePath()
    {
        var plist = LaunchAtLogin.MacPlist("/Applications/R&D <test>/Lumi.app");

        Assert.Contains("<string>io.github.neillog.lumi</string>", plist);
        Assert.Contains("<string>/usr/bin/open</string>", plist);
        Assert.Contains("<string>-a</string>", plist);
        Assert.Contains("<string>/Applications/R&amp;D &lt;test&gt;/Lumi.app</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>", plist);
        var document = System.Xml.Linq.XDocument.Parse(plist.Replace("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">", ""));
        Assert.Equal("plist", document.Root!.Name.LocalName);
    }

    [Fact]
    public void RunningFromDotnet_IsNotSupported_AndNeverEnabled()
    {
        var launch = new LaunchAtLogin(OperatingSystem.IsWindows() ? @"C:\Tools\dotnet.exe" : "/usr/local/share/dotnet/dotnet", _directory);

        Assert.False(launch.IsSupported);
        Assert.Throws<InvalidOperationException>(() => launch.SetEnabled(true));
    }

    [SkippableFact]
    public void MacOS_EnablingWritesTheLaunchAgent_AndDisablingRemovesIt()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "LaunchAgent 只在 macOS 上有意義");
        var launch = new LaunchAtLogin("/Applications/Lumi.app/Contents/MacOS/Lumi.App", _directory);
        var plist = Path.Combine(_directory, "io.github.neillog.lumi.plist");

        Assert.True(launch.IsSupported);
        Assert.False(launch.IsEnabled());

        launch.SetEnabled(true);
        Assert.True(launch.IsEnabled());
        Assert.Contains("/Applications/Lumi.app", File.ReadAllText(plist));

        launch.SetEnabled(true);   // 再開一次＝更新位置，不會重複或出錯
        Assert.True(launch.IsEnabled());

        launch.SetEnabled(false);
        Assert.False(launch.IsEnabled());
        Assert.False(File.Exists(plist));
        launch.SetEnabled(false);   // 已經關閉再關閉也沒事
    }
}
