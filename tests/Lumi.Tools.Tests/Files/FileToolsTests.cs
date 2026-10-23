using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Tools;
using Xunit;

namespace Lumi.Tools.Tests.Files;

public sealed class FileToolsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FileToolOptions _options = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private ToolContext Context(params ScopeRoot[] roots) => new("c1", ChatMode.Workbench, TimeProvider.System,
        new FileScope(roots.Length > 0 ? roots : [new ScopeRoot("專案", _tree.Root, true)]));

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private Task<ToolResult> Read(object args, ToolContext? context = null) =>
        new ReadFileTool(_options).ExecuteAsync(Json(args), context ?? Context(), default);

    private Task<ToolResult> List(object args, ToolContext? context = null) =>
        new ListDirTool(_options).ExecuteAsync(Json(args), context ?? Context(), default);

    private Task<ToolResult> Search(object args, ToolContext? context = null) =>
        new SearchTool(_options).ExecuteAsync(Json(args), context ?? Context(), default);

    // ================= read_file =================

    [Fact]
    public async Task ReadFile_ReturnsLinesWithNumbers_AndSummary()
    {
        _tree.Write("a.txt", "第一行\n第二行\n第三行\n");

        var result = await Read(new { path = "a.txt" });

        Assert.False(result.IsError);
        Assert.StartsWith("a.txt（共 3 行）", result.Content);
        Assert.Contains("     1\t第一行", result.Content);
        Assert.Contains("     3\t第三行", result.Content);
        Assert.Equal("3 行", result.Summary);
    }

    [Fact]
    public async Task ReadFile_OffsetAndLimit_ReturnAWindow_AndTellHowToContinue()
    {
        _tree.Write("a.txt", string.Join('\n', Enumerable.Range(1, 10).Select(i => $"行{i}")));

        var result = await Read(new { path = "a.txt", offset = 3, limit = 4 });

        Assert.Contains("（第 3–6 行，共 10 行）", result.Content);
        Assert.Contains("     3\t行3", result.Content);
        Assert.Contains("     6\t行6", result.Content);
        Assert.DoesNotContain("行7", result.Content);
        Assert.Contains("用 offset=7 繼續讀", result.Content);
        Assert.Equal("第 3–6 行 / 共 10 行", result.Summary);
    }

    [Fact]
    public async Task ReadFile_OffsetPastTheEnd_IsAnError()
    {
        _tree.Write("a.txt", "一\n二");

        var result = await Read(new { path = "a.txt", offset = 99 });

        Assert.True(result.IsError);
        Assert.Contains("共 2 行", result.Content);
    }

    [Fact]
    public async Task ReadFile_OffsetBelowOne_IsAnError()
    {
        _tree.Write("a.txt", "一");

        Assert.True((await Read(new { path = "a.txt", offset = 0 })).IsError);
    }

    [Fact]
    public async Task ReadFile_TruncatesByCharacterBudget_AndSaysWhereToContinue()
    {
        _options.MaxReadChars = 100;
        _tree.Write("big.txt", string.Join('\n', Enumerable.Range(1, 100).Select(i => new string('x', 30))));

        var result = await Read(new { path = "big.txt" });

        Assert.False(result.IsError);
        Assert.True(result.Content.Length < 400);
        Assert.Contains("共 100 行", result.Content);
        Assert.Contains("繼續讀", result.Content);
    }

    [Fact]
    public async Task ReadFile_ALongSingleLine_IsClipped_NotDropped()
    {
        _tree.Write("min.js", new string('x', 10_000));

        var result = await Read(new { path = "min.js" });

        Assert.False(result.IsError);
        Assert.Contains("此行過長，已截斷", result.Content);
        Assert.True(result.Content.Length < 2_500);
    }

    [Fact]
    public async Task ReadFile_EmptyFile_SaysSo()
    {
        _tree.Write("empty.txt", "");

        var result = await Read(new { path = "empty.txt" });

        Assert.False(result.IsError);
        Assert.Contains("空檔案", result.Content);
    }

    [Fact]
    public async Task ReadFile_HandlesCrLfAndChineseAndEmoji()
    {
        _tree.Write("win.txt", "中文 ❤️‍🔥\r\n第二行\r\n");

        var result = await Read(new { path = "win.txt" });

        Assert.Contains("中文 ❤️‍🔥", result.Content);
        Assert.DoesNotContain('\r', result.Content);
        Assert.Contains("共 2 行", result.Content);
    }

    [Fact]
    public async Task ReadFile_Utf8Bom_IsStripped()
    {
        File.WriteAllText(_tree.Path_("bom.txt"), "內容", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var result = await Read(new { path = "bom.txt" });

        Assert.Contains("\t內容", result.Content);
        Assert.DoesNotContain('﻿', result.Content);
    }

    [Fact]
    public async Task ReadFile_Utf16WithBom_IsDecoded()
    {
        File.WriteAllText(_tree.Path_("u16.txt"), "你好\n世界", new UnicodeEncoding(false, true));

        var result = await Read(new { path = "u16.txt" });

        Assert.False(result.IsError);
        Assert.Contains("你好", result.Content);
        Assert.Contains("世界", result.Content);
    }

    [Fact]
    public async Task ReadFile_BinaryFile_IsRefusedWithAnExplanation()
    {
        File.WriteAllBytes(_tree.Path_("a.bin"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02]);

        var result = await Read(new { path = "a.bin" });

        Assert.True(result.IsError);
        Assert.Contains("不是文字檔", result.Content);
    }

    [Fact]
    public async Task ReadFile_InvalidUtf8_IsRefusedInsteadOfGarbled()
    {
        File.WriteAllBytes(_tree.Path_("latin1.txt"), Encoding.Latin1.GetBytes("café ÿþ bad"));

        var result = await Read(new { path = "latin1.txt" });

        Assert.True(result.IsError);
        Assert.Contains("UTF-8", result.Content);
    }

    [Fact]
    public async Task ReadFile_TooLargeFile_PointsToSearch()
    {
        _options.MaxReadFileBytes = 50;
        _tree.Write("big.txt", new string('a', 200));

        var result = await Read(new { path = "big.txt" });

        Assert.True(result.IsError);
        Assert.Contains("search", result.Content);
    }

    [Fact]
    public async Task ReadFile_Directory_PointsToListDir()
    {
        _tree.Dir("src");

        var result = await Read(new { path = "src" });

        Assert.True(result.IsError);
        Assert.Contains("list_dir", result.Content);
    }

    [Fact]
    public async Task ReadFile_Missing_IsAnError()
    {
        var result = await Read(new { path = "nope.txt" });

        Assert.True(result.IsError);
        Assert.Contains("找不到", result.Content);
    }

    [Fact]
    public async Task ReadFile_OutsideScope_IsDeniedWithAReasonTheModelCanSee()
    {
        var secretOutside = _outside.Write("x.txt", "不該被讀到");

        var byAbsolute = await Read(new { path = secretOutside });
        var byDotDot = await Read(new { path = "../" + Path.GetFileName(_outside.Root) + "/x.txt" });

        Assert.True(byAbsolute.IsError);
        Assert.Contains("不在可存取的範圍內", byAbsolute.Content);
        Assert.DoesNotContain("不該被讀到", byAbsolute.Content);
        Assert.True(byDotDot.IsError);
        Assert.DoesNotContain("不該被讀到", byDotDot.Content);
    }

    [Fact]
    public async Task ReadFile_SecretFile_IsDenied()
    {
        _tree.Write(".env", "API_KEY=sk-ant-PLAINTEXT");

        var result = await Read(new { path = ".env" });

        Assert.True(result.IsError);
        Assert.Contains("機密", result.Content);
        Assert.DoesNotContain("sk-ant", result.Content);
    }

    [Fact]
    public async Task ReadFile_ExplicitlyAddedSecretFile_IsReadable()
    {
        var file = _tree.Write(".env", "A=1");

        var result = await Read(new { path = ".env" }, Context(new ScopeRoot(".env", file, false)));

        Assert.False(result.IsError);
        Assert.Contains("A=1", result.Content);
    }

    [Fact]
    public async Task ReadFile_WithoutScope_ExplainsThereIsNothingToRead()
    {
        var result = await new ReadFileTool(_options).ExecuteAsync(Json(new { path = "a.txt" }),
            new ToolContext("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance), default);

        Assert.True(result.IsError);
        Assert.Contains("沒有加入", result.Content);
    }

    [Fact]
    public async Task ReadFile_UnicodeFileName_Works()
    {
        _tree.Write("資料夾/筆記 📝.md", "好");

        var result = await Read(new { path = "資料夾/筆記 📝.md" });

        Assert.False(result.IsError);
        Assert.Contains("\t好", result.Content);
    }

    [Fact]
    public async Task ReadFile_FileBeingWrittenByAnotherProcess_IsStillReadable()
    {
        var path = _tree.Write("log.txt", "第一行\n");
        await using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var result = await Read(new { path = "log.txt" });

        Assert.False(result.IsError);
    }

    [Fact]
    public void ReadFile_PreviewShowsThePath()
    {
        Assert.Equal("src/a.cs", new ReadFileTool(_options).Preview(Json(new { path = " src/a.cs " }), Context()).Target);
    }

    [Fact]
    public async Task ReadFile_MissingPathArgument_IsAnErrorResult()
    {
        var result = await new ReadFileTool(_options).ExecuteAsync("{}", Context(), default);

        Assert.True(result.IsError);
    }

    // ================= list_dir =================

    [Fact]
    public async Task ListDir_ListsFoldersFirst_WithSizes()
    {
        _tree.Write("b.txt", "12345");
        _tree.Write("a.txt", "x");
        _tree.Dir("zeta");
        _tree.Dir("alpha");

        var result = await List(new { });

        Assert.False(result.IsError);
        var lines = result.Content.Split('\n');
        Assert.Equal("（範圍的根）", lines[0]);
        Assert.Equal(["  alpha/", "  zeta/", "  a.txt  (1 B)", "  b.txt  (5 B)"], lines.Skip(1).Take(4));
        Assert.Equal("4 個項目", result.Summary);
    }

    [Fact]
    public async Task ListDir_Depth_ShowsNestedEntriesIndented()
    {
        _tree.Write("src/app/main.cs");
        _tree.Write("src/readme.md");

        var shallow = await List(new { path = "src" });
        var deep = await List(new { path = "src", depth = 2 });

        Assert.DoesNotContain("main.cs", shallow.Content);
        Assert.Contains("  app/", shallow.Content);
        Assert.Contains("    main.cs", deep.Content);
        Assert.StartsWith("src/", deep.Content);
    }

    [Fact]
    public async Task ListDir_DepthIsClamped()
    {
        _tree.Write("a/b/c/d/e/deep.txt");

        var result = await List(new { depth = 99 });

        Assert.Contains("c/", result.Content);
        Assert.DoesNotContain("d/", result.Content);   // 上限 3 層
    }

    [Fact]
    public async Task ListDir_SkipsIgnoredFolders_AndMentionsThem()
    {
        _tree.Write("node_modules/x/index.js");
        _tree.Write(".git/config");
        _tree.Write("bin/Debug/a.dll");
        _tree.Write("src/a.cs");

        var result = await List(new { depth = 2 });

        Assert.Contains("src/", result.Content);
        Assert.DoesNotContain("index.js", result.Content);
        Assert.DoesNotContain("a.dll", result.Content);
        Assert.Contains("已略過 3 個項目", result.Content);
        Assert.Contains(".git", result.Content);
        Assert.Contains("node_modules", result.Content);
    }

    [Fact]
    public async Task ListDir_MarksSecretFiles_WithoutHidingTheNames()
    {
        _tree.Write(".env", "A=1");
        _tree.Write("deploy.pem", "x");
        _tree.Write(".env.example", "A=");

        var result = await List(new { });

        Assert.Contains(".env  (3 B)（機密，不會讀取）", result.Content);
        Assert.Contains("deploy.pem  (1 B)（機密，不會讀取）", result.Content);
        Assert.Contains(".env.example  (2 B)\n", result.Content + "\n");
    }

    [Fact]
    public async Task ListDir_OfficeAndPdfFiles_SayToUseReadDocument_OthersDoNot()
    {
        _tree.Write("report.docx", "x");
        _tree.Write("Data.XLSX", "x");
        _tree.Write("notes.txt", "x");
        _tree.Write("legacy.doc", "x");

        var result = await List(new { });

        Assert.Contains("  Data.XLSX  (1 B)（可用 read_document 讀取）\n", result.Content + "\n");
        Assert.Contains("  report.docx  (1 B)（可用 read_document 讀取）\n", result.Content + "\n");
        Assert.Contains("  notes.txt  (1 B)\n", result.Content + "\n");
        Assert.Contains("  legacy.doc  (1 B)\n", result.Content + "\n");
    }

    [Fact]
    public async Task ListDir_StopsAtTheEntryLimit_AndSaysSo()
    {
        _options.ListMaxEntries = 5;
        for (var i = 0; i < 20; i++)
            _tree.Write($"f{i:D2}.txt");

        var result = await List(new { });

        Assert.Equal(5, result.Content.Split('\n').Count(l => l.StartsWith("  f")));
        Assert.Contains("上限", result.Content);
    }

    [Fact]
    public async Task ListDir_EmptyFolder_SaysSo()
    {
        _tree.Dir("empty");

        var result = await List(new { path = "empty" });

        Assert.Contains("（空的）", result.Content);
    }

    [Fact]
    public async Task ListDir_OnAFile_DescribesTheFile()
    {
        _tree.Write("a.txt", "12345");

        var result = await List(new { path = "a.txt" });

        Assert.False(result.IsError);
        Assert.Contains("是檔案", result.Content);
    }

    [Fact]
    public async Task ListDir_MissingFolder_IsAnError() =>
        Assert.True((await List(new { path = "nope" })).IsError);

    [Fact]
    public async Task ListDir_OutsideScope_IsDenied()
    {
        var result = await List(new { path = _outside.Root });

        Assert.True(result.IsError);
        Assert.Contains("不在可存取的範圍內", result.Content);
    }

    [Fact]
    public async Task ListDir_MultipleRoots_WithoutPath_ListsTheRoots()
    {
        var file = _tree.Write("notes.md");
        var context = Context(new ScopeRoot("甲", _tree.Dir("a"), true), new ScopeRoot("乙", _tree.Dir("b"), true), new ScopeRoot("notes.md", file, false));

        var result = await List(new { }, context);

        Assert.Contains("甲/", result.Content);
        Assert.Contains("乙/", result.Content);
        Assert.Contains("  notes.md\n", result.Content + "\n");
        Assert.Equal("3 個範圍", result.Summary);
    }

    [Fact]
    public async Task ListDir_MultipleRoots_ListsInsideARootByName()
    {
        _tree.Write("a/x.txt");
        var context = Context(new ScopeRoot("甲", _tree.Path_("a"), true), new ScopeRoot("乙", _tree.Dir("b"), true));

        var result = await List(new { path = "甲" }, context);

        Assert.Contains("x.txt", result.Content);
        Assert.StartsWith("甲/", result.Content);
    }

    [SkippableFact]
    public async Task ListDir_SymlinkPointingOutside_IsShownButNotFollowed()
    {
        _outside.Write("data/secret.txt");
        Skip.IfNot(_tree.TryLinkDirectory("escape", _outside.Root), "這個環境不能建立符號連結");

        var result = await List(new { depth = 3 });

        Assert.Contains("escape → （指向範圍外，不會跟隨）", result.Content);
        Assert.DoesNotContain("secret.txt", result.Content);
    }

    [SkippableFact]
    public async Task ListDir_SymlinkLoopInsideScope_DoesNotRunForever()
    {
        _tree.Dir("a");
        Skip.IfNot(_tree.TryLinkDirectory("a/loop", _tree.Root), "這個環境不能建立符號連結");

        var result = await List(new { depth = 3 });

        Assert.False(result.IsError);
    }

    // ================= search =================

    [Fact]
    public async Task Search_FindsContent_WithPathAndLineNumber()
    {
        _tree.Write("src/a.cs", "class A\n{\n    // TODO: 修好\n}\n");
        _tree.Write("src/b.cs", "// 沒有\n");

        var result = await Search(new { pattern = "TODO" });

        Assert.False(result.IsError);
        Assert.Equal("src/a.cs:3: // TODO: 修好", result.Content.Split('\n')[0]);
        Assert.Equal("1 筆符合 / 掃描 2 個檔案", result.Summary);
    }

    [Fact]
    public async Task Search_IsCaseSensitiveByDefault_AndCanIgnoreCase()
    {
        _tree.Write("a.txt", "Hello\nhello\n");

        var exact = await Search(new { pattern = "hello" });
        var loose = await Search(new { pattern = "hello", ignore_case = true });

        Assert.Equal("1 筆符合 / 掃描 1 個檔案", exact.Summary);
        Assert.Equal("2 筆符合 / 掃描 1 個檔案", loose.Summary);
    }

    [Fact]
    public async Task Search_Glob_RestrictsFiles_ByNameOrPath()
    {
        _tree.Write("a.cs", "needle");
        _tree.Write("b.txt", "needle");
        _tree.Write("src/deep/c.cs", "needle");
        _tree.Write("other/d.cs", "needle");

        var byName = await Search(new { pattern = "needle", glob = "*.cs" });
        var byPath = await Search(new { pattern = "needle", glob = "src/**/*.cs" });

        Assert.Equal(["a.cs:1: needle", "other/d.cs:1: needle", "src/deep/c.cs:1: needle"], byName.Content.Split('\n').Order());
        Assert.Equal("src/deep/c.cs:1: needle", byPath.Content);
    }

    [Fact]
    public async Task Search_GlobOnly_ListsMatchingFileNames()
    {
        _tree.Write("a.md");
        _tree.Write("docs/b.md");
        _tree.Write("c.txt");

        var result = await Search(new { glob = "*.md" });

        Assert.Equal(["a.md", "docs/b.md"], result.Content.Split('\n').Order());
    }

    [Fact]
    public async Task Search_RestrictedToAPath()
    {
        _tree.Write("src/a.cs", "needle");
        _tree.Write("test/b.cs", "needle");

        var result = await Search(new { pattern = "needle", path = "src" });

        Assert.Equal("src/a.cs:1: needle", result.Content);
    }

    [Fact]
    public async Task Search_NeitherPatternNorGlob_IsAnError()
    {
        var result = await Search(new { });

        Assert.True(result.IsError);
    }

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("[a-")]
    [InlineData("*bad")]
    public async Task Search_InvalidRegex_IsAnErrorResult(string pattern)
    {
        var result = await Search(new { pattern });

        Assert.True(result.IsError);
        Assert.Contains("正規表示式", result.Content);
    }

    [Theory]
    [InlineData(@"(?=needle)needle")]
    [InlineData(@"(\w)\1")]
    public async Task Search_UnsupportedRegexFeatures_AreAnErrorResult(string pattern)
    {
        var result = await Search(new { pattern });

        Assert.True(result.IsError);
        Assert.Contains("不支援", result.Content);
    }

    [Fact]
    public async Task Search_PatternTooLong_IsRejected()
    {
        var result = await Search(new { pattern = new string('a', _options.SearchMaxPatternLength + 1) });

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Search_CatastrophicPattern_FinishesQuickly()
    {
        // (a+)+$ 在回溯式引擎上對 "aaaa…b" 是指數時間；NonBacktracking 是線性
        _tree.Write("a.txt", new string('a', 5_000) + "b");
        var stopwatch = Stopwatch.StartNew();

        var result = await Search(new { pattern = "(a+)+$" });

        Assert.False(result.IsError);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"花了 {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Search_StopsAtTheResultLimit_AndSaysSo()
    {
        _options.SearchMaxResults = 3;
        _tree.Write("a.txt", string.Join('\n', Enumerable.Repeat("hit", 10)));

        var result = await Search(new { pattern = "hit" });

        Assert.Equal(3, result.Content.Split('\n').Count(l => l.Contains(":hit") || l.Contains(": hit")));
        Assert.Contains("只顯示前 3 筆", result.Content);
    }

    [Fact]
    public async Task Search_StopsAtTheFileLimit_AndSaysSo()
    {
        _options.SearchMaxFiles = 3;
        for (var i = 0; i < 10; i++)
            _tree.Write($"f{i}.txt", "nothing");

        var result = await Search(new { pattern = "hit" });

        Assert.Contains("掃描到 3 個檔案就停了", result.Content);
    }

    [Fact]
    public async Task Search_SkipsBinaryLargeAndSecretFiles_AndReportsCounts()
    {
        _options.SearchMaxFileBytes = 100;
        File.WriteAllBytes(_tree.Path_("img.dat"), [1, 0, 2, 0, 3]);
        _tree.Write("huge.txt", "needle " + new string('x', 500));
        _tree.Write(".env", "needle=1");
        _tree.Write("ok.txt", "needle");

        var result = await Search(new { pattern = "needle" });

        Assert.Equal("ok.txt:1: needle", result.Content.Split('\n')[0]);
        Assert.DoesNotContain(".env:", result.Content);
        Assert.Contains("1 個過大", result.Content);
        Assert.Contains("1 個非文字檔", result.Content);
        Assert.Contains("1 個機密檔案", result.Content);
    }

    [Fact]
    public async Task Search_IgnoresVcsAndBuildFolders()
    {
        _tree.Write(".git/objects/x", "needle");
        _tree.Write("node_modules/p/i.js", "needle");
        _tree.Write("bin/a.txt", "needle");
        _tree.Write("src/a.txt", "needle");

        var result = await Search(new { pattern = "needle" });

        Assert.Equal("src/a.txt:1: needle", result.Content);
    }

    [Fact]
    public async Task Search_LongMatchingLines_AreClipped()
    {
        _tree.Write("a.txt", "needle " + new string('x', 5_000));

        var result = await Search(new { pattern = "needle" });

        Assert.True(result.Content.Length < 400);
    }

    [Fact]
    public async Task Search_NoMatches_SaysSoWithoutError()
    {
        _tree.Write("a.txt", "nothing here");

        var result = await Search(new { pattern = "needle" });

        Assert.False(result.IsError);
        Assert.Contains("沒有找到", result.Content);
        Assert.Equal("沒有符合", result.Summary);
    }

    [Fact]
    public async Task Search_OutsideScopePath_IsDenied()
    {
        _outside.Write("a.txt", "needle");

        var result = await Search(new { pattern = "needle", path = _outside.Root });

        Assert.True(result.IsError);
        Assert.Contains("不在可存取的範圍內", result.Content);
    }

    [Fact]
    public async Task Search_MultipleRoots_PrefixesResultsWithTheRootName()
    {
        _tree.Write("a/x.txt", "needle");
        _tree.Write("b/y.txt", "needle");
        var context = Context(new ScopeRoot("甲", _tree.Path_("a"), true), new ScopeRoot("乙", _tree.Path_("b"), true));

        var result = await Search(new { pattern = "needle" }, context);

        Assert.Equal(["乙/y.txt:1: needle", "甲/x.txt:1: needle"], result.Content.Split('\n').Order());
    }

    [Fact]
    public async Task Search_SingleFileScope_OnlySearchesThatFile()
    {
        var file = _tree.Write("only.txt", "needle");
        _tree.Write("other.txt", "needle");

        var result = await Search(new { pattern = "needle" }, Context(new ScopeRoot("only.txt", file, false)));

        Assert.Equal("only.txt:1: needle", result.Content);
    }

    [SkippableFact]
    public async Task Search_DoesNotFollowSymlinksOutOfScope()
    {
        _outside.Write("secret.txt", "needle");
        Skip.IfNot(_tree.TryLinkDirectory("escape", _outside.Root), "這個環境不能建立符號連結");
        Skip.IfNot(_tree.TryLinkFile("leak.txt", _outside.Path_("secret.txt")), "這個環境不能建立符號連結");
        _tree.Write("ok.txt", "needle");

        var result = await Search(new { pattern = "needle" });

        Assert.Equal("ok.txt:1: needle", result.Content);
    }

    [Theory]
    [InlineData("*.cs", "a.cs", true)]
    [InlineData("*.cs", "A.CS", true)]
    [InlineData("*.cs", "a.csx", false)]
    [InlineData("a?.txt", "ab.txt", true)]
    [InlineData("a?.txt", "a/.txt", false)]
    [InlineData("src/**/*.ts", "src/a/b/c.ts", true)]
    [InlineData("src/**/*.ts", "src/c.ts", true)]
    [InlineData("src/*.ts", "src/a/c.ts", false)]
    [InlineData("**/*.md", "docs/x/y.md", true)]
    [InlineData("file(1).txt", "file(1).txt", true)]
    [InlineData("a.b", "aXb", false)]
    public void GlobToRegex_Matches(string glob, string path, bool expected) =>
        Assert.Equal(expected, SearchTool.GlobToRegex(glob).IsMatch(path));

    // ================= 名單 =================

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".ENV", true)]
    [InlineData(".env.local", true)]
    [InlineData(".env.example", false)]
    [InlineData(".env.sample", false)]
    [InlineData("server.pem", true)]
    [InlineData("app.key", true)]
    [InlineData("x.pfx", true)]
    [InlineData("x.p12", true)]
    [InlineData("id_rsa", true)]
    [InlineData("id_rsa.pub", true)]
    [InlineData("id_ed25519", true)]
    [InlineData("secrets.json", true)]
    [InlineData("appsettings.json", false)]
    [InlineData("keyboard.cs", false)]
    [InlineData("environment.md", false)]
    public void SecretFileNames(string name, bool expected) => Assert.Equal(expected, ScopeRules.IsSecretFileName(name));

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".ssh", true)]
    [InlineData(".github", false)]
    [InlineData("src", false)]
    public void SecretDirectories(string name, bool expected) => Assert.Equal(expected, ScopeRules.IsSecretDirectory(name));
}
