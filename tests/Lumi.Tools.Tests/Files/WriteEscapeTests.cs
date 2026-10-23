using Lumi.Core.Tools;
using Xunit;
using static Lumi.Tools.Tests.Files.WriteTestSupport;

namespace Lumi.Tools.Tests.Files;

/// <summary>
/// 所有寫入類工具的逃逸手法：<c>..</c>、範圍外的絕對路徑、符號連結逃逸（資料夾與檔案、懸空的連結）、機密檔名、
/// 大小寫與尾端分隔字元。一律失敗，而且範圍外不會多出任何檔案、既有檔案內容不變。
/// </summary>
public sealed class WriteEscapeTests : IDisposable
{
    private static readonly string[] WriteTools = ["write_file", "overwrite_file", "copy_file", "move_file", "create_folder"];

    /// <summary>目的（或要寫入的路徑）的寫法。<c>{ABS}</c> 換成範圍外的資料夾，<c>{OUT}</c> 換成它的名稱。</summary>
    private static readonly string[] EscapePaths =
    [
        "../{OUT}/new.txt",
        "sub/../../{OUT}/new.txt",
        "{ABS}/new.txt",
        "..",
        "../",
        "{ABS}-sibling/new.txt",
        "escape/new.txt",
        "escape/deeper/new.txt",
        "dangling.txt",
        "linkfile.txt",
        ".env",
        ".ENV",
        "config/.env.production",
        "x/.git/config",
        ".Git/hooks/post-commit",
        ".ssh/authorized_keys",
        "secrets.json",
        "keys/server.PEM",
    ];

    /// <summary>來源（copy_file、move_file）的寫法：要讀範圍外或機密的檔案，也必須失敗。</summary>
    private static readonly string[] EscapeSources =
    [
        "{ABS}/secret.txt",
        "../{OUT}/secret.txt",
        "escape/secret.txt",
        "linkfile.txt",
        "dangling.txt",
    ];

    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly string _secret;

    public WriteEscapeTests()
    {
        _secret = _outside.Write("secret.txt", "機密");
    }

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    public static IEnumerable<object[]> WriteCases() =>
        from tool in WriteTools
        from path in EscapePaths
        select new object[] { tool, path };

    public static IEnumerable<object[]> SourceCases() =>
        from tool in new[] { "copy_file", "move_file" }
        from source in EscapeSources
        select new object[] { tool, source };

    /// <summary>範圍內的兩個連結：資料夾指向範圍外、檔案指向範圍外的既有檔案、懸空的連結指向範圍外。</summary>
    private bool CreateLinks() =>
        _tree.TryLinkDirectory("escape", _outside.Root)
        && _tree.TryLinkFile("linkfile.txt", _secret)
        && _tree.TryLinkFile("dangling.txt", _outside.Path_("planned.txt"));

    private string Expand(string template) =>
        template.Replace("{OUT}", Path.GetFileName(_outside.Root)).Replace("{ABS}", _outside.Root);

    private Task<ToolResult> Run(string tool, string path)
    {
        var context = Context(ScopeOf(_tree));
        var options = new FileToolOptions();
        return tool switch
        {
            "write_file" => new WriteFileTool(options).ExecuteAsync(Json(new { path, mode = "append", text = "入侵" }), context, default),
            "overwrite_file" => new OverwriteFileTool(options).ExecuteAsync(Json(new { path, text = "入侵" }), context, default),
            "copy_file" => new CopyFileTool().ExecuteAsync(Json(new { source_path = "inside.txt", destination_path = path }), context, default),
            "move_file" => new MoveFileTool().ExecuteAsync(Json(new { source_path = "inside.txt", destination_path = path }), context, default),
            "create_folder" => new CreateFolderTool().ExecuteAsync(Json(new { path }), context, default),
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    [SkippableTheory]
    [MemberData(nameof(WriteCases))]
    public async Task EscapingDestinations_Fail_AndNothingAppearsOutsideTheScope(string tool, string template)
    {
        Skip.IfNot(CreateLinks(), "這個環境不能建立符號連結");
        _tree.Write("inside.txt", "裡面");
        var before = Snapshot(_outside.Root);

        var result = await Run(tool, Expand(template));

        Assert.True(result.IsError, $"{tool} 不應該接受「{template}」，但回傳成功：{result.Content}");
        Assert.Equal(before, Snapshot(_outside.Root));
        Assert.Equal("機密", File.ReadAllText(_secret));
        Assert.Equal("裡面", File.ReadAllText(_tree.Path_("inside.txt")));
    }

    [SkippableTheory]
    [MemberData(nameof(SourceCases))]
    public async Task EscapingSources_Fail_AndNothingIsCopiedInOrMoved(string tool, string template)
    {
        Skip.IfNot(CreateLinks(), "這個環境不能建立符號連結");
        var before = Snapshot(_tree.Root);
        var context = Context(ScopeOf(_tree));
        var source = Expand(template);

        var result = tool == "copy_file"
            ? await new CopyFileTool().ExecuteAsync(Json(new { source_path = source, destination_path = "stolen.txt" }), context, default)
            : await new MoveFileTool().ExecuteAsync(Json(new { source_path = source, destination_path = "stolen.txt" }), context, default);

        Assert.True(result.IsError, $"{tool} 不應該讀取「{template}」，但回傳成功：{result.Content}");
        Assert.False(File.Exists(_tree.Path_("stolen.txt")));
        Assert.Equal("機密", File.ReadAllText(_secret));
        Assert.Equal(before, Snapshot(_tree.Root));
    }

    [SkippableFact]
    public async Task Overwrite_ThroughAFileLinkToOutside_LeavesTheTargetAlone()
    {
        Skip.IfNot(CreateLinks(), "這個環境不能建立符號連結");

        var result = await Run("overwrite_file", "linkfile.txt");

        Assert.True(result.IsError);
        Assert.Equal("機密", File.ReadAllText(_secret));
    }

    [SkippableFact]
    public async Task WriteAppend_ThroughADanglingLinkToOutside_DoesNotCreateTheTarget()
    {
        // 懸空的連結：目標（範圍外）還不存在。以前會被當成「不存在的範圍內路徑」，寫入就會建到範圍外
        Skip.IfNot(CreateLinks(), "這個環境不能建立符號連結");

        var result = await Run("write_file", "dangling.txt");

        Assert.True(result.IsError);
        Assert.False(File.Exists(_outside.Path_("planned.txt")));
    }

    [SkippableFact]
    public async Task EveryWriteTool_RefusesWhenTheScopeIsEmpty()
    {
        // 沒有範圍：工具本身也不能寫（請求層面的過濾是另一道防線，這裡確認工具自己也擋）
        var context = Context(EmptyFileScope.Instance);
        var write = await new WriteFileTool(new FileToolOptions()).ExecuteAsync(Json(new { path = "a.txt", mode = "create", text = "x" }), context, default);
        var folder = await new CreateFolderTool().ExecuteAsync(Json(new { path = "d" }), context, default);

        Assert.True(write.IsError);
        Assert.True(folder.IsError);
        Assert.Empty(Snapshot(_tree.Root));
    }
}
