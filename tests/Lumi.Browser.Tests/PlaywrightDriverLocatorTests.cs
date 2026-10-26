using Lumi.Browser;

namespace Lumi.Browser.Tests;

public sealed class PlaywrightDriverLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-pw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string MakeExtraction(string appName, string hash, string platform, bool withNode = true)
    {
        var dir = Path.Combine(_root, appName, hash);
        Directory.CreateDirectory(dir);
        if (withNode)
        {
            var node = PlaywrightDriverLocator.NodePath(dir, platform);
            Directory.CreateDirectory(Path.GetDirectoryName(node)!);
            File.WriteAllText(node, "node");
        }
        return dir;
    }

    [Fact]
    public void FindExtractionRoot_PicksTheFolderThatContainsNode()
    {
        MakeExtraction("Lumi-1.0.0-osx-arm64", "aaa", "darwin-arm64", withNode: false);
        var good = MakeExtraction("Lumi-1.0.0-osx-arm64", "bbb", "darwin-arm64");

        var found = PlaywrightDriverLocator.FindExtractionRoot([_root], "Lumi-1.0.0-osx-arm64", "darwin-arm64");

        Assert.Equal(good, found);
    }

    [Fact]
    public void FindExtractionRoot_PrefersTheNewestFolder()
    {
        var older = MakeExtraction("Lumi", "old", "win32_x64");
        Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-3));
        var newer = MakeExtraction("Lumi", "new", "win32_x64");

        Assert.Equal(newer, PlaywrightDriverLocator.FindExtractionRoot([_root], "Lumi", "win32_x64"));
    }

    [Fact]
    public void FindExtractionRoot_ReturnsNull_WhenNothingMatches()
    {
        MakeExtraction("Other", "aaa", "darwin-arm64");

        Assert.Null(PlaywrightDriverLocator.FindExtractionRoot([_root], "Lumi", "darwin-arm64"));
        Assert.Null(PlaywrightDriverLocator.FindExtractionRoot([_root], null, "darwin-arm64"));
        Assert.Null(PlaywrightDriverLocator.FindExtractionRoot([Path.Combine(_root, "missing")], "Lumi", "darwin-arm64"));
    }

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void EnsureExecutable_AddsTheExecuteBit_OnUnix()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows 沒有執行權限位元");
        var node = Path.Combine(_root, "node");
        Directory.CreateDirectory(_root);
        File.WriteAllText(node, "node");
        File.SetUnixFileMode(node, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        PlaywrightDriverLocator.EnsureExecutable(node);

        Assert.True(File.GetUnixFileMode(node).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void EnsureExecutable_IgnoresAMissingFile()
    {
        PlaywrightDriverLocator.EnsureExecutable(Path.Combine(_root, "missing-node"));
    }
}
