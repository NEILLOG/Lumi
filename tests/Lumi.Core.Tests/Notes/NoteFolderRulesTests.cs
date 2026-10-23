using Lumi.Core.Models;
using Lumi.Core.Notes;

namespace Lumi.Core.Tests.Notes;

/// <summary>M10 10B：筆記資料夾的設定規則（空字串＝預設、環境變數展開、不合法退回預設）。</summary>
public sealed class NoteFolderRulesTests : IDisposable
{
    private readonly string _variable = "LUMI_NOTE_TEST_" + Guid.NewGuid().ToString("N");
    private readonly string _base = Path.Combine(Path.GetTempPath(), "lumi-note-rules-" + Guid.NewGuid().ToString("N"));

    public NoteFolderRulesTests() => Environment.SetEnvironmentVariable(_variable, _base);

    public void Dispose() => Environment.SetEnvironmentVariable(_variable, null);

    [Fact]
    public void DefaultFolder_IsLumiNotesUnderDocuments()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(Path.Combine(documents, "Lumi", "notes"), NoteFolderRules.DefaultFolder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_UsesDefault_WithoutProblem(string? setting)
    {
        var resolution = NoteFolderRules.Resolve(setting);

        Assert.Equal(NoteFolderRules.DefaultFolder, resolution.Folder);
        Assert.Null(resolution.Problem);
    }

    [Fact]
    public void AbsolutePath_IsUsed_WithSurroundingSpacesTrimmed()
    {
        var path = Path.Combine(_base, "my notes");

        var resolution = NoteFolderRules.Resolve($"  {path}  ");

        Assert.Equal(Path.GetFullPath(path), resolution.Folder);
        Assert.Null(resolution.Problem);
    }

    [Fact]
    public void PercentVariable_IsExpanded()
    {
        var resolution = NoteFolderRules.Resolve($"%{_variable}%{Path.DirectorySeparatorChar}notes");

        Assert.Equal(Path.GetFullPath(Path.Combine(_base, "notes")), resolution.Folder);
        Assert.Null(resolution.Problem);
    }

    [Fact]
    public void DollarVariable_AndBracedVariable_AreExpanded()
    {
        var dollar = NoteFolderRules.Resolve($"${_variable}/notes");
        var braced = NoteFolderRules.Resolve($"${{{_variable}}}/notes");

        Assert.Equal(Path.GetFullPath(Path.Combine(_base, "notes")), dollar.Folder);
        Assert.Equal(dollar.Folder, braced.Folder);
    }

    [Fact]
    public void MissingVariable_FallsBackToDefault_AndNamesTheVariable()
    {
        var resolution = NoteFolderRules.Resolve("$LUMI_NOTE_TEST_MISSING_" + Guid.NewGuid().ToString("N"));

        Assert.Equal(NoteFolderRules.DefaultFolder, resolution.Folder);
        Assert.NotNull(resolution.Problem);
        Assert.Contains("環境變數", resolution.Problem);
    }

    [Fact]
    public void RelativePath_FallsBackToDefault_WithProblem()
    {
        var resolution = NoteFolderRules.Resolve("notes/inside");

        Assert.Equal(NoteFolderRules.DefaultFolder, resolution.Folder);
        Assert.Contains("完整路徑", resolution.Problem);
    }

    [Fact]
    public void InvalidCharacter_FallsBackToDefault_WithProblem()
    {
        var resolution = NoteFolderRules.Resolve(Path.Combine(_base, "bad\0name"));

        Assert.Equal(NoteFolderRules.DefaultFolder, resolution.Folder);
        Assert.NotNull(resolution.Problem);
    }

    [Fact]
    public void Validate_AcceptsEmptyAndGoodPaths_RejectsBadOnesWithReason()
    {
        Assert.Null(NoteFolderRules.Validate(""));
        Assert.Null(NoteFolderRules.Validate(Path.Combine(_base, "ok")));
        Assert.Null(NoteFolderRules.Validate($"${_variable}"));

        var reason = NoteFolderRules.Validate("relative");
        Assert.NotNull(reason);
        Assert.Contains("不合法", reason);
        Assert.DoesNotContain("改用預設", reason);
    }

    [Fact]
    public void NotesSettings_Normalize_TrimsAndTreatsNullAsEmpty()
    {
        Assert.Equal(new NotesSettings("a/b"), new NotesSettings("  a/b ").Normalize());
        Assert.Equal(NotesSettings.Default, new NotesSettings(null!).Normalize());
    }
}
