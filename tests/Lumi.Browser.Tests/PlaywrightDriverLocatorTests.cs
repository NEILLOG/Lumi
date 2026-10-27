using System.IO.Compression;
using Lumi.Browser;

namespace Lumi.Browser.Tests;

public sealed class PlaywrightDriverLocatorTests : IDisposable
{
    private const string Platform = "darwin-arm64";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-pw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>做一個像內嵌 zip 的資料流：<c>.playwright/node/&lt;平台&gt;/node</c> 加一個 JS 套件檔。</summary>
    private static byte[] BuildZip(string nodeContent = "node")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, $".playwright/node/{Platform}/node", nodeContent);
            Write(zip, ".playwright/package/package.json", "{}");
        }
        return buffer.ToArray();

        static void Write(ZipArchive zip, string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }
    }

    private sealed class CountingDriver(string hash, byte[] zip)
    {
        public int Opened { get; private set; }

        public PlaywrightDriverLocator.EmbeddedDriver Driver => new(hash, () =>
        {
            Opened++;
            return new MemoryStream(zip);
        });
    }

    private string InstallRoot => Path.Combine(_root, "playwright");

    [Fact]
    public void EnsureExtracted_ExtractsTheDriverAndWritesTheMarker()
    {
        var source = new CountingDriver("aaaa1111aaaa1111", BuildZip());

        var dir = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, source.Driver, Platform);

        Assert.Equal(Path.Combine(InstallRoot, "aaaa1111aaaa1111"), dir);
        Assert.True(File.Exists(PlaywrightDriverLocator.NodePath(dir, Platform)));
        Assert.True(File.Exists(Path.Combine(dir, ".playwright", "package", "package.json")));
        Assert.True(File.Exists(Path.Combine(dir, PlaywrightDriverLocator.CompleteMarker)));
        Assert.Equal([dir], Directory.GetDirectories(InstallRoot));   // 沒有留下暫存資料夾
    }

    [Fact]
    public void EnsureExtracted_ReusesAnAlreadyExtractedFolder()
    {
        var source = new CountingDriver("aaaa1111aaaa1111", BuildZip());
        var first = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, source.Driver, Platform);
        File.WriteAllText(Path.Combine(first, "user-marker.txt"), "不能被覆蓋");

        var second = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, source.Driver, Platform);

        Assert.Equal(first, second);
        Assert.Equal(1, source.Opened);
        Assert.True(File.Exists(Path.Combine(second, "user-marker.txt")));
    }

    [Fact]
    public void EnsureExtracted_ReextractsAFolderWithoutTheMarker()
    {
        // 上次解到一半（沒有 .complete）：不能直接用，要重解
        var half = Path.Combine(InstallRoot, "aaaa1111aaaa1111");
        Directory.CreateDirectory(Path.Combine(half, ".playwright", "node", Platform));
        File.WriteAllText(Path.Combine(half, "stale.txt"), "殘留");
        var source = new CountingDriver("aaaa1111aaaa1111", BuildZip("完整的 node"));

        var dir = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, source.Driver, Platform);

        Assert.Equal(half, dir);
        Assert.Equal(1, source.Opened);
        Assert.True(File.Exists(Path.Combine(dir, PlaywrightDriverLocator.CompleteMarker)));
        Assert.Equal("完整的 node", File.ReadAllText(PlaywrightDriverLocator.NodePath(dir, Platform)));
        Assert.False(File.Exists(Path.Combine(dir, "stale.txt")));
        Assert.Single(Directory.GetDirectories(InstallRoot));
    }

    [Fact]
    public void EnsureExtracted_DoesNotLeaveAFolderBehind_WhenTheZipIsBroken()
    {
        var broken = new PlaywrightDriverLocator.EmbeddedDriver("bbbb2222bbbb2222", () => new MemoryStream([1, 2, 3, 4]));

        Assert.ThrowsAny<Exception>(() => PlaywrightDriverLocator.EnsureExtracted(InstallRoot, broken, Platform));

        Assert.Empty(Directory.GetDirectories(InstallRoot));
    }

    [Fact]
    public void EnsureExtracted_GivesEachHashItsOwnFolder_AndKeepsTheOldOne()
    {
        var older = new CountingDriver("aaaa1111aaaa1111", BuildZip("舊版"));
        var newer = new CountingDriver("bbbb2222bbbb2222", BuildZip("新版"));

        var oldDir = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, older.Driver, Platform);
        var newDir = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, newer.Driver, Platform);

        Assert.NotEqual(oldDir, newDir);
        Assert.Equal("舊版", File.ReadAllText(PlaywrightDriverLocator.NodePath(oldDir, Platform)));
        Assert.Equal("新版", File.ReadAllText(PlaywrightDriverLocator.NodePath(newDir, Platform)));
    }

    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void EnsureExtracted_MakesNodeExecutable_OnUnix()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows 沒有執行權限位元");
        var source = new CountingDriver("aaaa1111aaaa1111", BuildZip());

        var dir = PlaywrightDriverLocator.EnsureExtracted(InstallRoot, source.Driver, Platform);

        // zip 裡的檔案沒有記錄執行權限：解完要補上
        Assert.True(File.GetUnixFileMode(PlaywrightDriverLocator.NodePath(dir, Platform)).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public void FindEmbedded_ReadsTheHashFromTheResourceName()
    {
        var found = PlaywrightDriverLocator.FindEmbedded([typeof(PlaywrightDriverLocatorTests).Assembly]);

        Assert.NotNull(found);
        Assert.Equal("0123456789abcdef", found.Hash);
        using var stream = found.Open();
        using var reader = new StreamReader(stream);
        Assert.Equal("embedded sample", reader.ReadToEnd());
    }

    [Fact]
    public void FindEmbedded_ReturnsNull_WhenNoAssemblyHasTheResource()
    {
        Assert.Null(PlaywrightDriverLocator.FindEmbedded([typeof(PlaywrightDriverLocator).Assembly, null]));
        Assert.Null(PlaywrightDriverLocator.FindEmbedded([]));
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
