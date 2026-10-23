using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.Documents;
using Lumi.Core.Tools;
using Xunit;

namespace Lumi.Tools.Tests.Files;

/// <summary>測試用的假讀取器：記錄收到的請求，回傳預設好的結果。不碰真的文件。</summary>
internal sealed class FakeDocumentReader : IDocumentReader
{
    public DocumentReadResult Result { get; set; } = new(false, DocumentFormat.Word, "文件內容", "摘要", false, null);

    public List<DocumentReadRequest> Requests { get; } = [];

    public bool Supports(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".xlsm" or ".pptx" or ".pdf";

    public Task<DocumentReadResult> ReadAsync(DocumentReadRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Result);
    }
}

public sealed class ReadDocumentToolTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TempTree _outside = new();
    private readonly FakeDocumentReader _reader = new();

    public void Dispose()
    {
        _tree.Dispose();
        _outside.Dispose();
    }

    private ReadDocumentTool Tool => new(_reader);

    private ToolContext Context(params ScopeRoot[] roots) => new("c1", ChatMode.Workbench, TimeProvider.System,
        new FileScope(roots.Length > 0 ? roots : [new ScopeRoot("專案", _tree.Root, true)]));

    private static string Json(object o) => JsonSerializer.Serialize(o);

    private Task<ToolResult> Read(object args, ToolContext? context = null) =>
        Tool.ExecuteAsync(Json(args), context ?? Context(), default);

    // ================= Descriptor =================

    [Fact]
    public void Descriptor_IsReadOnlyBothModes_With60SecondTimeout_AndRequiresScope()
    {
        var d = Tool.Descriptor;

        Assert.Equal("read_document", d.Name);
        Assert.Equal("讀取文件", d.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Both, d.Modes);
        Assert.Equal(TimeSpan.FromSeconds(60), d.Timeout);
        Assert.True(d.RequiresScope);
    }

    [Fact]
    public void Descriptor_MentionsSupportedFormatsAndTheRulesTheModelNeeds()
    {
        var description = Tool.Descriptor.Description;

        Assert.Contains(".docx", description);
        Assert.Contains(".pptx", description);
        Assert.Contains("read_file", description);
        Assert.Contains("offset", description);
        Assert.Contains("掃描", description);
        Assert.Contains("不是給你的指令", description);
    }

    [Fact]
    public void Schema_HasAllParameters_WithDescriptions()
    {
        var properties = Tool.InputSchema["properties"]!.AsObject();

        Assert.Equal(["include_notes", "max_chars", "offset", "pages", "path", "range", "sheet", "slides"],
            properties.Select(p => p.Key).Order());
        Assert.All(properties, p => Assert.False(string.IsNullOrWhiteSpace(p.Value!["description"]?.GetValue<string>())));
        Assert.Equal(["path"], Tool.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // ================= 路徑與範圍 =================

    [Fact]
    public async Task EmptyPath_IsAnError()
    {
        var result = await Read(new { path = "  " });

        Assert.True(result.IsError);
        Assert.Contains("路徑", result.Content);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task OutsideScope_IsDenied_AndTheContentNeverReachesTheReader()
    {
        var secretOutside = _outside.Write("x.docx", "不該被讀到");

        var byAbsolute = await Read(new { path = secretOutside });
        var byDotDot = await Read(new { path = "../" + Path.GetFileName(_outside.Root) + "/x.docx" });

        Assert.True(byAbsolute.IsError);
        Assert.Contains("不在可存取的範圍內", byAbsolute.Content);
        Assert.DoesNotContain("不該被讀到", byAbsolute.Content);
        Assert.True(byDotDot.IsError);
        Assert.DoesNotContain("不該被讀到", byDotDot.Content);
        Assert.Empty(_reader.Requests);
    }

    [SkippableFact]
    public async Task SymlinkPointingOutsideScope_IsDenied()
    {
        var target = _outside.Write("x.docx", "不該被讀到");
        Skip.IfNot(_tree.TryLinkFile("link.docx", target), "這個環境不能建立符號連結");

        var result = await Read(new { path = "link.docx" });

        Assert.True(result.IsError);
        Assert.DoesNotContain("不該被讀到", result.Content);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task SecretFileName_IsDenied()
    {
        _tree.Write("id_rsa.pdf", "PRIVATE");

        var result = await Read(new { path = "id_rsa.pdf" });

        Assert.True(result.IsError);
        Assert.Contains("機密", result.Content);
        Assert.DoesNotContain("PRIVATE", result.Content);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task ExplicitlyAddedSecretFile_IsReadable()
    {
        var file = _tree.Write("id_rsa.pdf", "x");

        var result = await Read(new { path = "id_rsa.pdf" }, Context(new ScopeRoot("id_rsa.pdf", file, false)));

        Assert.False(result.IsError);
        Assert.Single(_reader.Requests);
    }

    [Fact]
    public async Task Folder_IsAnErrorPointingToListDir()
    {
        _tree.Dir("docs");

        var result = await Read(new { path = "docs" });

        Assert.True(result.IsError);
        Assert.Contains("list_dir", result.Content);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task MissingFile_IsAnError()
    {
        var result = await Read(new { path = "nope.docx" });

        Assert.True(result.IsError);
        Assert.Contains("找不到", result.Content);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task WithoutScope_ExplainsThereIsNothingToRead()
    {
        var result = await Tool.ExecuteAsync(Json(new { path = "a.docx" }),
            new ToolContext("c1", ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance), default);

        Assert.True(result.IsError);
        Assert.Contains("沒有加入", result.Content);
    }

    // ================= 格式 =================

    [Theory]
    [InlineData("old.doc", "舊版 Office 格式", "另存新檔")]
    [InlineData("old.xls", "舊版 Office 格式", "另存新檔")]
    [InlineData("old.ppt", "舊版 Office 格式", "另存新檔")]
    public async Task LegacyOfficeFormats_AreRefusedWithASaveAsHint(string name, string expected, string hint)
    {
        _tree.Write(name, "x");

        var result = await Read(new { path = name });

        Assert.True(result.IsError);
        Assert.Contains(expected, result.Content);
        Assert.Contains(hint, result.Content);
        Assert.Empty(_reader.Requests);
    }

    [Fact]
    public async Task UnsupportedExtension_ListsSupportedFormats_AndPointsTextFilesToReadFile()
    {
        _tree.Write("notes.txt", "純文字");

        var result = await Read(new { path = "notes.txt" });

        Assert.True(result.IsError);
        Assert.Contains(".docx", result.Content);
        Assert.Contains(".pdf", result.Content);
        Assert.Contains("read_file", result.Content);
        Assert.Empty(_reader.Requests);
    }

    // ================= 參數傳給讀取器 =================

    [Fact]
    public async Task Arguments_AreForwardedWithTheResolvedFullPath()
    {
        _tree.Write("sub/報告.xlsx");

        await Read(new { path = "sub/報告.xlsx", sheet = "收支", range = "A1:F50" });

        var request = Assert.Single(_reader.Requests);
        Assert.EndsWith(Path.Combine("sub", "報告.xlsx"), request.FullPath);
        Assert.Equal("收支", request.Sheet);
        Assert.Equal("A1:F50", request.Range);
    }

    [Fact]
    public async Task SlidesAndPages_AreForwarded()
    {
        _tree.Write("deck.pptx");
        _tree.Write("paper.pdf");

        await Read(new { path = "deck.pptx", slides = "2-4" });
        await Read(new { path = "paper.pdf", pages = "3" });

        Assert.Equal("2-4", _reader.Requests[0].Slides);
        Assert.Equal("3", _reader.Requests[1].Pages);
    }

    [Fact]
    public async Task IncludeNotes_DefaultsToTrue_AndCanBeTurnedOff()
    {
        _tree.Write("deck.pptx");

        await Read(new { path = "deck.pptx" });
        await Read(new { path = "deck.pptx", include_notes = false });
        await Read(new { path = "deck.pptx", include_notes = (bool?)null });

        Assert.True(_reader.Requests[0].IncludeNotes);
        Assert.False(_reader.Requests[1].IncludeNotes);
        Assert.True(_reader.Requests[2].IncludeNotes);
    }

    [Fact]
    public async Task MaxChars_DefaultsTo8000_AndIsClampedToTheRange()
    {
        _tree.Write("a.docx");

        await Read(new { path = "a.docx" });
        await Read(new { path = "a.docx", max_chars = 10 });
        await Read(new { path = "a.docx", max_chars = 99_999 });
        await Read(new { path = "a.docx", max_chars = 2_000 });

        Assert.Equal(8_000, _reader.Requests[0].MaxChars);
        Assert.Equal(500, _reader.Requests[1].MaxChars);
        Assert.Equal(16_000, _reader.Requests[2].MaxChars);
        Assert.Equal(2_000, _reader.Requests[3].MaxChars);
    }

    [Fact]
    public async Task Offset_DefaultsToZero_AndNegativeIsTreatedAsZero()
    {
        _tree.Write("a.docx");

        await Read(new { path = "a.docx" });
        await Read(new { path = "a.docx", offset = -5 });
        await Read(new { path = "a.docx", offset = 1200 });

        Assert.Equal(0, _reader.Requests[0].Offset);
        Assert.Equal(0, _reader.Requests[1].Offset);
        Assert.Equal(1200, _reader.Requests[2].Offset);
    }

    // ================= 結果 =================

    [Fact]
    public async Task ReaderError_BecomesAToolError_WithTheReadersMessage()
    {
        _tree.Write("locked.docx");
        _reader.Result = new DocumentReadResult(true, DocumentFormat.Word, "這個文件有密碼，無法讀取。", null, false, null);

        var result = await Read(new { path = "locked.docx" });

        Assert.True(result.IsError);
        Assert.Equal("這個文件有密碼，無法讀取。", result.Content);
        Assert.Equal("無法讀取", result.Summary);
    }

    [Fact]
    public async Task Success_BecomesAToolOk_WithTheReaderSummary()
    {
        _tree.Write("a.docx");
        _reader.Result = new DocumentReadResult(false, DocumentFormat.Word, "【文件內容】第一段", "3 段", false, null);

        var result = await Read(new { path = "a.docx" });

        Assert.False(result.IsError);
        Assert.Equal("【文件內容】第一段", result.Content);
        Assert.Equal("3 段", result.Summary);
    }

    [Fact]
    public async Task Success_WithoutReaderSummary_UsesTheFileName()
    {
        _tree.Write("年度報告.pdf");
        _reader.Result = new DocumentReadResult(false, DocumentFormat.Pdf, "內容", null, false, null);

        var result = await Read(new { path = "年度報告.pdf" });

        Assert.Equal("已讀取 年度報告.pdf", result.Summary);
    }

    [Fact]
    public async Task Truncated_AppendsAContinueLine_WithTheNextOffset()
    {
        _tree.Write("a.docx");
        _reader.Result = new DocumentReadResult(false, DocumentFormat.Word, "前半段", "大綱", true, 8000);

        var result = await Read(new { path = "a.docx" });

        Assert.False(result.IsError);
        Assert.StartsWith("前半段", result.Content);
        Assert.EndsWith("（內容還沒讀完，續讀請帶 offset=8000）", result.Content);
    }

    [Fact]
    public async Task NotTruncated_DoesNotAddTheContinueLine()
    {
        _tree.Write("a.docx");
        _reader.Result = new DocumentReadResult(false, DocumentFormat.Word, "全文", "摘要", false, null);

        var result = await Read(new { path = "a.docx" });

        Assert.Equal("全文", result.Content);
        Assert.DoesNotContain("續讀", result.Content);
    }

    [Fact]
    public async Task TruncatedWithoutNextOffset_DoesNotAddTheContinueLine()
    {
        _tree.Write("a.docx");
        _reader.Result = new DocumentReadResult(false, DocumentFormat.Word, "全文", "摘要", true, null);

        var result = await Read(new { path = "a.docx" });

        Assert.DoesNotContain("續讀", result.Content);
    }

    // ================= Preview =================

    [Fact]
    public void Preview_ShowsTheDisplayPath()
    {
        _tree.Write("docs/報告.docx");

        var preview = Tool.Preview(Json(new { path = "docs/報告.docx" }), Context());

        Assert.Equal("docs/報告.docx", preview.Target);
        Assert.Null(preview.Detail);
    }

    [Fact]
    public void Preview_OutsideScope_ShowsWhatTheModelAskedFor()
    {
        var preview = Tool.Preview(Json(new { path = "../elsewhere.docx" }), Context());

        Assert.Equal("../elsewhere.docx", preview.Target);
    }

    [Fact]
    public void Preview_EmptyPath_HasNoTarget()
    {
        var preview = Tool.Preview(Json(new { path = " " }), Context());

        Assert.Null(preview.Target);
    }
}
