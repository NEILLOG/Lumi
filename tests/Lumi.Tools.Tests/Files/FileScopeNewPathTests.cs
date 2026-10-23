using Lumi.Core.Tools;
using Xunit;

namespace Lumi.Tools.Tests.Files;

/// <summary>寫入會用到「還不存在的路徑」：這些情境的解析結果必須正確（A2）。</summary>
public sealed class FileScopeNewPathTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private FileScope FolderScope() => new([new ScopeRoot("專案", _tree.Root, true)]);

    /// <summary>相對於範圍根的顯示路徑（不直接比絕對路徑：macOS 的 /var 會展開成 /private/var）。</summary>
    private string DisplayOf(ScopeResolution result) => FolderScope().ToDisplayPath(result.FullPath!);

    private static void AssertDenied(ScopeResolution result, string? contains = null)
    {
        Assert.False(result.IsAllowed);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        if (contains is not null)
            Assert.Contains(contains, result.Error);
    }

    // ---- 允許 ----

    [Fact]
    public void NewFile_DirectlyInsideTheRoot_IsAllowed_AndResolvesToThePlannedPath()
    {
        var result = FolderScope().Resolve("brand-new.txt");

        Assert.True(result.IsAllowed);
        Assert.Equal("brand-new.txt", DisplayOf(result));
    }

    [Fact]
    public void NewFile_UnderNonexistentFolders_IsAllowed()
    {
        var result = FolderScope().Resolve("a/b/c/new.md");

        Assert.True(result.IsAllowed);
        Assert.Equal("a/b/c/new.md", DisplayOf(result));
    }

    [Fact]
    public void NewFile_UnicodeName_IsAllowed() =>
        Assert.True(FolderScope().Resolve("購物清單 🛒.md").IsAllowed);

    [Fact]
    public void NewFile_WithDotDotThatStaysInside_IsAllowed()
    {
        var result = FolderScope().Resolve("missing/../new.txt");

        Assert.Equal("new.txt", DisplayOf(result));
    }

    [Fact]
    public void NewFile_WithTrailingSeparator_ResolvesWithoutIt()
    {
        // 尾端的分隔字元不會變成資料夾：解析結果是不帶分隔字元的檔案路徑（在範圍內）
        var result = FolderScope().Resolve("new.txt/");

        Assert.True(result.IsAllowed);
        Assert.Equal("new.txt", DisplayOf(result));
    }

    [Fact]
    public void NewFile_NameThatDiffersOnlyInCaseFromAnExistingFile_StaysInside()
    {
        _tree.Write("Readme.md");

        var result = FolderScope().Resolve("readme.md");

        Assert.True(result.IsAllowed);   // 不分大小寫的系統會對到既有檔案，區分大小寫的系統是新檔；兩者都在範圍內
        Assert.Equal("readme.md", DisplayOf(result).ToLowerInvariant());
    }

    // ---- 拒絕：跳出範圍 ----

    [Theory]
    [InlineData("../new.txt")]
    [InlineData("missing/../../new.txt")]
    [InlineData("..")]
    [InlineData("../")]
    public void NewFile_DotDotOutOfTheRoot_IsDenied(string path) =>
        AssertDenied(FolderScope().Resolve(path), "不在可存取的範圍內");

    [Fact]
    public void NewFile_AbsolutePathOutsideTheRoot_IsDenied() =>
        AssertDenied(FolderScope().Resolve(_outside.Path_("new.txt")), "不在可存取的範圍內");

    [Fact]
    public void NewFile_InASiblingWithTheSamePrefix_IsDenied() =>
        AssertDenied(FolderScope().Resolve(_tree.Root + "-sibling/new.txt"));

    [SkippableFact]
    public void NewFile_BelowASymlinkedFolderPointingOutside_IsDenied()
    {
        _outside.Dir("data");
        Skip.IfNot(_tree.TryLinkDirectory("escape", _outside.Root), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("escape/new.txt"), "不在可存取的範圍內");
        AssertDenied(FolderScope().Resolve("escape/data/deeper/new.txt"));
    }

    [SkippableFact]
    public void NewFile_BelowASymlinkedFolderThatPointsInside_IsAllowed_AndResolvesToTheRealFolder()
    {
        var real = _tree.Dir("real");
        Skip.IfNot(_tree.TryLinkDirectory("alias", real), "這個環境不能建立符號連結");

        var result = FolderScope().Resolve("alias/new.txt");

        Assert.True(result.IsAllowed);
        Assert.Equal("real/new.txt", DisplayOf(result));
    }

    [SkippableFact]
    public void NewFile_ThroughADanglingSymlinkPointingOutside_IsDenied()
    {
        // 懸空的符號連結（目標還不存在）：寫入會跟著連結建到範圍外，一定要擋
        Skip.IfNot(_tree.TryLinkFile("dangling.txt", _outside.Path_("new.txt")), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("dangling.txt"), "不在可存取的範圍內");
    }

    [SkippableFact]
    public void NewFile_ThroughADanglingChainEndingOutside_IsDenied()
    {
        Skip.IfNot(_tree.TryLinkFile("hop2.txt", _outside.Path_("later.txt")), "這個環境不能建立符號連結");
        Skip.IfNot(_tree.TryLinkFile("hop1.txt", _tree.Path_("hop2.txt")), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("hop1.txt"));
    }

    [SkippableFact]
    public void NewFile_ThroughADanglingSymlinkPointingInside_IsAllowed()
    {
        Skip.IfNot(_tree.TryLinkFile("alias.txt", _tree.Path_("planned.txt")), "這個環境不能建立符號連結");

        var result = FolderScope().Resolve("alias.txt");

        Assert.True(result.IsAllowed);
        Assert.Equal("planned.txt", DisplayOf(result));
    }

    [SkippableFact]
    public void NewFile_ThroughADanglingSymlinkWithARelativeTargetOutside_IsDenied()
    {
        // 相對路徑的連結目標：要相對於連結所在的資料夾解析，跳到範圍外就擋下
        var relative = Path.Combine("..", Path.GetFileName(_outside.Root), "new.txt");
        Skip.IfNot(_tree.TryLinkFile("rel.txt", relative), "這個環境不能建立符號連結");

        AssertDenied(FolderScope().Resolve("rel.txt"));
    }

    // ---- 拒絕：單一檔案的範圍 ----

    [Fact]
    public void SingleFileRoot_NewSiblingFile_IsDenied()
    {
        var file = _tree.Write("notes.md");
        var scope = new FileScope([new ScopeRoot("notes.md", file, false)]);

        AssertDenied(scope.Resolve("new.md"), "只有單一檔案");
        AssertDenied(scope.Resolve(_tree.Path_("new.md")));
        AssertDenied(scope.Resolve("../new.md"));
    }

    // ---- 拒絕：機密名單（新檔也一樣） ----

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.production")]
    [InlineData("config/.env")]
    [InlineData(".ENV")]
    [InlineData("keys/server.PEM")]
    [InlineData("secrets.json")]
    [InlineData("id_rsa_backup")]
    [InlineData(".git/hooks/new")]
    [InlineData(".Git/config")]
    [InlineData("a/.ssh/authorized_keys")]
    [InlineData(".SSH/notes.txt")]
    public void NewSecretFile_IsDenied(string path) =>
        AssertDenied(FolderScope().Resolve(path), "機密");

    [Theory]
    [InlineData(".env.example")]
    [InlineData("new.env.txt")]
    public void NewOrdinaryFileNames_AreAllowed(string path) =>
        Assert.True(FolderScope().Resolve(path).IsAllowed);

    // ---- 拒絕：不合法 ----

    [Theory]
    [InlineData("a\0b.txt")]
    public void NewFile_WithInvalidCharacters_IsDenied(string path) =>
        AssertDenied(FolderScope().Resolve(path));

    [Fact]
    public void NewFile_TildeIsAnOrdinaryFolderName_NotHome()
    {
        var result = FolderScope().Resolve("~/new.txt");

        Assert.True(result.IsAllowed);
        Assert.Equal("~/new.txt", DisplayOf(result));
    }
}
