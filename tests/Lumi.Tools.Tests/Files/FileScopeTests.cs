using Lumi.Core.Context;
using Lumi.Core.Tools;
using Xunit;

namespace Lumi.Tools.Tests.Files;

public sealed class FileScopeTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private FileScope FolderScope(FileScopeRules? rules = null, string? sub = null) =>
        new([new ScopeRoot("專案", sub is null ? _tree.Root : _tree.Path_(sub), true)], rules);

    private static void AssertDenied(ScopeResolution result, string? contains = null)
    {
        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        if (contains is not null)
            Assert.Contains(contains, result.Error);
    }

    // ---- 基本 ----

    [Fact]
    public void RelativePath_InsideRoot_IsAllowed()
    {
        var file = _tree.Write("src/a.cs");

        var result = FolderScope().Resolve("src/a.cs");

        Assert.True(result.IsAllowed);
        Assert.Equal(Path.GetFileName(file), Path.GetFileName(result.FullPath));
        Assert.Equal("專案", result.Root!.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("./")]
    public void RootItself_IsResolvable_WhenSingleRoot(string path) =>
        Assert.True(FolderScope().Resolve(path).IsAllowed);

    [Fact]
    public void AbsolutePath_InsideRoot_IsAllowed()
    {
        var file = _tree.Write("a.txt");

        Assert.True(FolderScope().Resolve(file).IsAllowed);
    }

    [Fact]
    public void PathThatDoesNotExistYet_InsideRoot_ResolvesButIsNotAnError()
    {
        // 解析只管「在不在範圍內」；檔案存不存在由工具自己回報
        Assert.True(FolderScope().Resolve("missing/none.txt").IsAllowed);
    }

    [Fact]
    public void NoRoots_DeniesEverything() =>
        AssertDenied(new FileScope([]).Resolve("a.txt"), "沒有加入");

    // ---- 跳出範圍 ----

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("a/b/../../../outside.txt")]
    [InlineData("..")]
    [InlineData("../")]
    public void DotDotEscape_IsDenied(string path)
    {
        _tree.Dir("src");
        AssertDenied(FolderScope(sub: "src").Resolve(path), "不在可存取的範圍內");
    }

    [Fact]
    public void DotDot_ThatStaysInside_IsAllowed()
    {
        _tree.Write("src/a.txt");
        _tree.Dir("src/deep");

        Assert.True(FolderScope().Resolve("src/deep/../a.txt").IsAllowed);
    }

    [Fact]
    public void AbsolutePath_OutsideRoot_IsDenied()
    {
        var other = _outside.Write("x.txt");

        AssertDenied(FolderScope().Resolve(other), "不在可存取的範圍內");
    }

    [Fact]
    public void SiblingFolderWithSamePrefix_IsNotInside()
    {
        // /root/proj 的範圍不能讀 /root/proj-secret（前綴相同但不是子路徑）
        var proj = _tree.Dir("proj");
        var sibling = _tree.Write("proj-secret/key.txt");
        var scope = new FileScope([new ScopeRoot("proj", proj, true)]);

        AssertDenied(scope.Resolve(sibling));
        AssertDenied(scope.Resolve("../proj-secret/key.txt"));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/")]
    public void SystemPaths_AreDenied(string path)
    {
        Skip.If(OperatingSystem.IsWindows());
        AssertDenied(FolderScope().Resolve(path));
    }

    [Fact]
    public void TildeIsNotExpanded()
    {
        // 「~」不是家目錄的縮寫：當成範圍內一個奇怪的資料夾名稱，不會跳到使用者的家目錄
        var result = FolderScope().Resolve("~/.ssh/id_rsa");

        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar + ".ssh", result.FullPath ?? "");
    }

    [Theory]
    [InlineData("a\0b")]
    public void NullCharacter_IsDenied(string path) => AssertDenied(FolderScope().Resolve(path), "不合法");

    // ---- 符號連結 ----

    [SkippableFact]
    public void SymlinkedFile_PointingOutside_IsDenied()
    {
        var target = _outside.Write("secret.txt", "機密");
        Skip.IfNot(_tree.TryLinkFile("link.txt", target), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("link.txt"), "不在可存取的範圍內");
    }

    [SkippableFact]
    public void SymlinkedDirectory_PointingOutside_IsDenied_ForEverythingBelowIt()
    {
        _outside.Write("data/x.txt");
        Skip.IfNot(_tree.TryLinkDirectory("escape", _outside.Root), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("escape"));
        AssertDenied(FolderScope().Resolve("escape/data/x.txt"));
    }

    [SkippableFact]
    public void SymlinkInside_PointingInside_IsAllowed_AndResolvesToRealPath()
    {
        var real = _tree.Write("real/a.txt");
        Skip.IfNot(_tree.TryLinkFile("alias.txt", real), "這個環境不能建立符號連結");

        var result = FolderScope().Resolve("alias.txt");

        Assert.True(result.IsAllowed);
        Assert.EndsWith(Path.Combine("real", "a.txt"), result.FullPath);
    }

    [SkippableFact]
    public void SymlinkChain_EndingOutside_IsDenied()
    {
        var target = _outside.Write("t.txt");
        Skip.IfNot(_tree.TryLinkFile("hop2.txt", target), "這個環境不能建立符號連結");
        Skip.IfNot(_tree.TryLinkFile("hop1.txt", _tree.Path_("hop2.txt")), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("hop1.txt"));
    }

    [SkippableFact]
    public void BrokenSymlink_IsDeniedWithoutThrowing()
    {
        Skip.IfNot(_tree.TryLinkFile("dangling.txt", _tree.Path_("nope.txt")), "這個環境不能建立符號連結");

        var result = FolderScope().Resolve("dangling.txt");

        // 目標不存在：不管解成允許或拒絕，都不能丟例外，而且不能落在範圍外
        if (result.IsAllowed)
            Assert.StartsWith(Path.GetFullPath(_tree.Root).TrimEnd(Path.DirectorySeparatorChar), result.FullPath!.Replace("/private", ""), StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void SymlinkLoop_IsDeniedWithoutHanging()
    {
        Skip.IfNot(_tree.TryLinkFile("a.link", _tree.Path_("b.link")), "這個環境不能建立符號連結");
        Skip.IfNot(_tree.TryLinkFile("b.link", _tree.Path_("a.link")), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("a.link"));
    }

    [SkippableFact]
    public void RootGivenThroughASymlink_StillWorks()
    {
        // 使用者加入的資料夾本身是符號連結（macOS 的 /tmp、/var 就是）：範圍是它指向的真實位置
        var real = _outside.Dir("project");
        _outside.Write("project/a.txt");
        Skip.IfNot(_tree.TryLinkDirectory("proj-link", real), "這個環境不能建立符號連結");
        var scope = new FileScope([new ScopeRoot("proj", _tree.Path_("proj-link"), true)]);

        Assert.True(scope.Resolve("a.txt").IsAllowed);
        Assert.True(scope.Resolve(Path.Combine(real, "a.txt")).IsAllowed);
    }

    [Fact]
    public void TempDirectoryUnderASystemSymlink_Works()
    {
        // macOS 的暫存資料夾在 /var → /private/var 底下：不能因為路徑表示不同就誤判成範圍外
        _tree.Write("a.txt");

        Assert.True(FolderScope().Resolve(Path.Combine(_tree.Root, "a.txt")).IsAllowed);
    }

    // ---- 單一檔案的範圍 ----

    [Fact]
    public void FileRoot_AllowsOnlyThatFile()
    {
        var file = _tree.Write("notes.md");
        _tree.Write("other.md");
        var scope = new FileScope([new ScopeRoot("notes.md", file, false)]);

        Assert.True(scope.Resolve("notes.md").IsAllowed);
        Assert.True(scope.Resolve(file).IsAllowed);
        Assert.True(scope.Resolve("").IsAllowed);
        AssertDenied(scope.Resolve("other.md"), "只有單一檔案");
        AssertDenied(scope.Resolve(_tree.Path_("other.md")));
        AssertDenied(scope.Resolve(_tree.Root));
    }

    [Fact]
    public void FileRoot_WithASecretName_IsAllowed_BecauseTheUserChoseIt()
    {
        var file = _tree.Write(".env", "KEY=1");
        var scope = new FileScope([new ScopeRoot(".env", file, false)]);

        Assert.True(scope.Resolve(file).IsAllowed);
    }

    // ---- 多個根 ----

    [Fact]
    public void MultipleRoots_RelativePathsStartWithTheRootName()
    {
        var a = _tree.Dir("a");
        var b = _tree.Dir("b");
        _tree.Write("a/x.txt");
        var file = _tree.Write("single.txt");
        var scope = new FileScope([new ScopeRoot("甲", a, true), new ScopeRoot("乙", b, true), new ScopeRoot("single.txt", file, false)]);

        Assert.True(scope.Resolve("甲/x.txt").IsAllowed);
        Assert.True(scope.Resolve("乙").IsAllowed);
        Assert.True(scope.Resolve("single.txt").IsAllowed);
        AssertDenied(scope.Resolve("x.txt"), "找不到範圍");
        AssertDenied(scope.Resolve(""), "甲、乙、single.txt");
        AssertDenied(scope.Resolve("甲/../../outside"));
        AssertDenied(scope.Resolve("single.txt/sub"));
    }

    [Fact]
    public void MultipleRoots_CannotReachAcrossRoots_ThroughDotDot()
    {
        var a = _tree.Dir("a");
        var b = _tree.Write("b/secret.txt");
        var scope = new FileScope([new ScopeRoot("甲", a, true)]);

        AssertDenied(scope.Resolve("../b/secret.txt"));
        Assert.False(scope.Resolve(b).IsAllowed);
    }

    // ---- 機密 ----

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".env.production")]
    [InlineData("config/server.pem")]
    [InlineData("certs/site.key")]
    [InlineData("a/b/cert.pfx")]
    [InlineData("cert.p12")]
    [InlineData("id_rsa")]
    [InlineData("keys/id_ed25519")]
    [InlineData("id_rsa.pub")]
    [InlineData("secrets.json")]
    [InlineData("deploy/Secrets.JSON")]
    [InlineData(".git/config")]
    [InlineData("sub/.git/HEAD")]
    [InlineData(".ssh/known_hosts")]
    public void SecretFiles_AreDenied(string path)
    {
        _tree.Write(path);

        AssertDenied(FolderScope().Resolve(path), "機密");
    }

    [Theory]
    [InlineData(".env.example")]
    [InlineData(".env.sample")]
    [InlineData(".env.template")]
    [InlineData("README.md")]
    [InlineData("src/keyboard.cs")]
    [InlineData("monkey.txt")]
    [InlineData("id_rsa_notes_are_fine/readme.md")]
    public void OrdinaryFiles_AreAllowed(string path)
    {
        _tree.Write(path);

        // 注意：id_rsa* 是檔名前綴規則，資料夾名稱不受影響
        Assert.True(FolderScope().Resolve(path).IsAllowed);
    }

    [Fact]
    public void SecretRule_AppliesBelowTheRoot_NotToTheRootsOwnName()
    {
        // 使用者加入的資料夾本身叫 .git（或在 .ssh 底下）：範圍是使用者選的，只檢查根「底下」的部分
        var root = _tree.Dir("project/.ssh-notes");
        _tree.Write("project/.ssh-notes/a.txt");
        var scope = new FileScope([new ScopeRoot("n", root, true)]);

        Assert.True(scope.Resolve("a.txt").IsAllowed);
    }

    // ---- 平台規則 ----

    [Theory]
    [InlineData(@"\\server\share\a.txt")]
    [InlineData(@"\\?\C:\a.txt")]
    [InlineData("//server/share")]
    [InlineData("CON")]
    [InlineData("src/NUL.txt")]
    [InlineData("com1")]
    [InlineData("LPT9.log")]
    [InlineData("a.txt:stream")]
    [InlineData("dir/file.")]
    [InlineData("dir/file ")]
    public void WindowsRules_RejectDangerousPaths(string path) =>
        Assert.NotNull(FileScope.CheckWindowsRules(path));

    [Theory]
    [InlineData(@"C:\Users\me\a.txt")]
    [InlineData("src/a.cs")]
    [InlineData("console.txt")]
    [InlineData("auxiliary/x")]
    [InlineData("../up")]
    public void WindowsRules_AllowOrdinaryPaths(string path) =>
        Assert.Null(FileScope.CheckWindowsRules(path));

    [Fact]
    public void WindowsRules_AreAppliedByResolve_WhenEnabled()
    {
        var scope = FolderScope(new FileScopeRules(CaseInsensitive: true, WindowsPathRules: true, NormalizeUnicode: false));

        AssertDenied(scope.Resolve("NUL"));
        AssertDenied(scope.Resolve("a.txt:hidden"));
    }

    // ---- 大小寫、Unicode ----

    [Fact]
    public void CaseSensitiveRules_TreatDifferentCaseAsDifferentPaths()
    {
        var scope = new FileScope([new ScopeRoot("r", _tree.Dir("Proj"), true)],
            new FileScopeRules(CaseInsensitive: false, WindowsPathRules: false, NormalizeUnicode: false));

        AssertDenied(scope.Resolve(_tree.Root + Path.DirectorySeparatorChar + "proj" + Path.DirectorySeparatorChar + "a.txt"));
    }

    [Fact]
    public void CaseInsensitiveRules_AcceptDifferentCase()
    {
        var scope = new FileScope([new ScopeRoot("r", _tree.Dir("Proj"), true)],
            new FileScopeRules(CaseInsensitive: true, WindowsPathRules: false, NormalizeUnicode: false));

        Assert.True(scope.Resolve(_tree.Root + Path.DirectorySeparatorChar + "PROJ" + Path.DirectorySeparatorChar + "a.txt").IsAllowed);
    }

    [Fact]
    public void UnicodeNormalization_NfcAndNfdNamesMatch_WhenEnabled()
    {
        var nfd = "café".Normalize(System.Text.NormalizationForm.FormD);
        var nfc = "café".Normalize(System.Text.NormalizationForm.FormC);
        var scope = new FileScope([new ScopeRoot(nfd, _tree.Dir("x"), true), new ScopeRoot("另一個", _tree.Dir("y"), true)],
            new FileScopeRules(CaseInsensitive: true, WindowsPathRules: false, NormalizeUnicode: true));

        Assert.True(scope.Resolve(nfc + "/a.txt").IsAllowed);   // 模型給 NFC、名稱是 NFD
    }

    // ---- 顯示用路徑 ----

    [Fact]
    public void ToDisplayPath_SingleRoot_IsRelativeWithForwardSlashes()
    {
        var file = _tree.Write("src/deep/a.cs");

        Assert.Equal("src/deep/a.cs", FolderScope().ToDisplayPath(file));
        Assert.Equal("", FolderScope().ToDisplayPath(_tree.Root));
    }

    [Fact]
    public void ToDisplayPath_MultipleRoots_StartsWithTheRootName()
    {
        var a = _tree.Dir("a");
        var file = _tree.Write("a/x.txt");
        var scope = new FileScope([new ScopeRoot("甲", a, true), new ScopeRoot("乙", _tree.Dir("b"), true)]);

        Assert.Equal("甲/x.txt", scope.ToDisplayPath(file));
        Assert.Equal("甲", scope.ToDisplayPath(a));
    }

    [Fact]
    public void ToDisplayPath_OutsideScope_ReturnsTheInput()
    {
        var outside = _outside.Write("x.txt");

        Assert.Equal(outside, FolderScope().ToDisplayPath(outside));
    }

    // ---- 工廠 ----

    [Fact]
    public void Factory_BuildsRoots_SkipsImages_AndMakesNamesUnique()
    {
        var items = new[]
        {
            new ContextItem("1", ContextKind.Folder, _tree.Dir("p/src"), "src", DateTimeOffset.UtcNow, null),
            new ContextItem("2", ContextKind.Folder, _tree.Dir("q/src"), "src", DateTimeOffset.UtcNow, null),
            new ContextItem("3", ContextKind.File, _tree.Write("n.md"), "n.md", DateTimeOffset.UtcNow, null),
            new ContextItem("4", ContextKind.Image, _tree.Write("shot.png"), "shot.png", DateTimeOffset.UtcNow, null),
        };

        var scope = new FileScopeFactory().Create(items);

        Assert.Equal(["src", "src (2)", "n.md"], scope.Roots.Select(r => r.Name));
        Assert.Equal([true, true, false], scope.Roots.Select(r => r.IsDirectory));
    }

    [Fact]
    public void Factory_WithNoItems_ReturnsAnEmptyScope()
    {
        var scope = new FileScopeFactory().Create([]);

        Assert.False(scope.HasRoots);
        Assert.False(scope.Resolve("a").IsAllowed);
    }

    [Fact]
    public void Factory_WithOnlyImages_ReturnsAnEmptyScope() =>
        Assert.False(new FileScopeFactory().Create([new ContextItem("1", ContextKind.Image, "/x.png", "x", DateTimeOffset.UtcNow, null)]).HasRoots);
}
