using Lumi.Core.Chat;
using Lumi.Core.Notes;
using Lumi.Core.Tools;
using Lumi.Tools.Notes;

namespace Lumi.Tools.Tests.Notes;

/// <summary>M10 10B：<c>add_note</c>（記筆記）與 <c>read_notes</c>（查筆記）的參數驗證、錯誤訊息與結果格式。</summary>
public sealed class NoteToolsTests : IDisposable
{
    // 2026-10-23 09:05（UTC）
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 9, 5, 0, TimeSpan.Zero);

    private readonly NoteTempRoot _root = new();
    private readonly FolderBox _folder;
    private readonly NoteOptions _options = new();
    private readonly NoteStore _store;

    public NoteToolsTests()
    {
        _folder = new FolderBox(_root.Dir("notes"));
        _store = new NoteStore(_folder, _options);
    }

    public void Dispose() => _root.Dispose();

    private static ToolContext At(DateTimeOffset now, TimeZoneInfo? zone = null) =>
        new("c1", ChatMode.Workbench, new ClockAt(now, zone), EmptyFileScope.Instance);

    private AddNoteTool AddTool(INoteStore? store = null) => new(store ?? _store, _options);

    private ReadNotesTool ReadTool(INoteStore? store = null) => new(store ?? _store, _options);

    private string FileOf(DateOnly date) => Path.Combine(_folder.Folder, date.ToString("yyyy-MM-dd") + ".md");

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private static DateOnly Day() => new(2026, 10, 23);

    // ---- add_note ----

    [Fact]
    public async Task AddNote_AppendsToToday_WithCurrentTime_AndCategory()
    {
        var result = await AddTool().ExecuteAsync("""{"content":"買牛奶","category":"  開發 [x] "}""", At(Now), default);

        Assert.False(result.IsError);
        Assert.Equal("已記錄到 2026-10-23 的筆記。", result.Content);
        Assert.Equal("已記到 10/23 的筆記", result.Summary);
        Assert.Equal("# 2026-10-23\n\n- 09:05 [開發 x] 買牛奶\n", NoteFiles.ReadRaw(FileOf(Day())));
    }

    [Fact]
    public async Task AddNote_UsesTheLocalTimeZone_ForDateAndTime()
    {
        // 世界時 10/23 23:30 在台北是 10/24 07:30：今天與時間都以本機為準
        var taipei = TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei");
        var now = new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.Zero);

        var result = await AddTool().ExecuteAsync("""{"content":"時區"}""", At(now, taipei), default);

        Assert.Equal("已記錄到 2026-10-24 的筆記。", result.Content);
        Assert.Contains("- 07:30 時區", NoteFiles.ReadRaw(FileOf(new DateOnly(2026, 10, 24))));
    }

    [Fact]
    public async Task AddNote_ExplicitDate_IsUsed()
    {
        var result = await AddTool().ExecuteAsync("""{"content":"補記","date":"2026-10-20"}""", At(Now), default);

        Assert.Equal("已記錄到 2026-10-20 的筆記。", result.Content);
        Assert.True(File.Exists(FileOf(new DateOnly(2026, 10, 20))));
        Assert.False(File.Exists(FileOf(Day())));
    }

    [Theory]
    [InlineData("""{"content":""}""")]
    [InlineData("""{"content":"   \n  "}""")]
    public async Task AddNote_BlankContent_IsError(string json)
    {
        var result = await AddTool().ExecuteAsync(json, At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal("content 不可為空。", result.Content);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"content":null}""")]
    public async Task AddNote_MissingContent_IsArgumentError(string json)
    {
        // content 是必填參數（Schema 的 required）：缺少時由工具基底回參數錯誤，不會寫檔
        var result = await AddTool().ExecuteAsync(json, At(Now), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
        Assert.False(Directory.Exists(_folder.Folder));
    }

    [Fact]
    public async Task AddNote_ContentOverLimit_IsError_AtLimit_IsOk()
    {
        var atLimit = await AddTool().ExecuteAsync(Json(new { content = new string('a', 4000) }), At(Now), default);
        Assert.False(atLimit.IsError);

        var over = await AddTool().ExecuteAsync(Json(new { content = new string('a', 4001) }), At(Now), default);
        Assert.True(over.IsError);
        Assert.Equal("content 太長（上限 4000 字），請精簡後再記。", over.Content);
    }

    [Fact]
    public async Task AddNote_CategoryLimit_IsCheckedAfterNormalizing()
    {
        // 去掉括號之後是 50 字：可以
        var fifty = new string('類', 50);
        var ok = await AddTool().ExecuteAsync(Json(new { content = "x", category = "[" + fifty + "]" }), At(Now), default);
        Assert.False(ok.IsError);

        var tooLong = await AddTool().ExecuteAsync(Json(new { content = "x", category = new string('類', 51) }), At(Now), default);
        Assert.True(tooLong.IsError);
        Assert.Equal("category 太長（上限 50 字）。", tooLong.Content);
    }

    [Theory]
    [InlineData("[]", null)]
    [InlineData("   ", null)]
    [InlineData("[ 會議 ]", "會議")]
    [InlineData("a   b\t c", "a b c")]
    public void NormalizeCategory_RemovesBracketsAndCollapsesSpaces(string raw, string? expected)
    {
        Assert.Equal(expected, AddNoteTool.NormalizeCategory(raw));
    }

    [Fact]
    public async Task AddNote_CategoryOnlyBrackets_WritesNoCategory()
    {
        await AddTool().ExecuteAsync("""{"content":"沒有分類","category":"[]"}""", At(Now), default);

        Assert.Equal("# 2026-10-23\n\n- 09:05 沒有分類\n", NoteFiles.ReadRaw(FileOf(Day())));
    }

    [Theory]
    [InlineData("2026/10/23")]
    [InlineData("2026-10-5")]
    [InlineData("10-23-2026")]
    [InlineData("明天")]
    public async Task AddNote_BadDateFormat_IsError(string date)
    {
        var result = await AddTool().ExecuteAsync(Json(new { content = "x", date }), At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal("date 格式必須是 yyyy-MM-dd（例如 2026-09-30）。", result.Content);
    }

    [Fact]
    public async Task AddNote_DateRange_IsTodayMinus30_ThroughTomorrow()
    {
        var tool = AddTool();

        Assert.False((await tool.ExecuteAsync("""{"content":"x","date":"2026-09-23"}""", At(Now), default)).IsError);
        Assert.False((await tool.ExecuteAsync("""{"content":"x","date":"2026-10-24"}""", At(Now), default)).IsError);

        var tooOld = await tool.ExecuteAsync("""{"content":"x","date":"2026-09-22"}""", At(Now), default);
        Assert.True(tooOld.IsError);
        Assert.Equal("date 只能是 2026-09-23 到 2026-10-24 之間。", tooOld.Content);

        var tooNew = await tool.ExecuteAsync("""{"content":"x","date":"2026-10-25"}""", At(Now), default);
        Assert.True(tooNew.IsError);
    }

    [Fact]
    public async Task AddNote_WhenStoreFails_ReturnsStoreMessage()
    {
        var result = await AddTool(new FailingNoteStore("無法寫入筆記，測試")).ExecuteAsync("""{"content":"x"}""", At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal("無法寫入筆記：無法寫入筆記，測試", result.Content);
    }

    [Fact]
    public async Task AddNote_FileAtTheSizeLimit_IsWritten_OneMoreEntryIsRejected()
    {
        const int limit = 256 * 1024;
        Directory.CreateDirectory(_folder.Folder);
        // 既有檔案 = 上限 - 10 位元組；"- 09:05 x\n" 是 10 位元組
        File.WriteAllText(FileOf(Day()), "# 2026-10-23\n\n- 09:00 " + new string('a', limit - 33) + "\n");

        var atLimit = await AddTool().ExecuteAsync("""{"content":"x"}""", At(Now), default);
        Assert.False(atLimit.IsError);
        Assert.Equal(limit, new FileInfo(FileOf(Day())).Length);

        var over = await AddTool().ExecuteAsync("""{"content":"x"}""", At(Now), default);
        Assert.True(over.IsError);
        Assert.Equal("無法寫入筆記：這天的筆記已經太長（上限 256 KB），請改記到其他日期。", over.Content);
        Assert.Equal(limit, new FileInfo(FileOf(Day())).Length);
    }

    [Fact]
    public async Task AddNote_RealFileProblem_IsAnErrorResult_NotAnException()
    {
        // 資料夾的位置被檔案佔住
        File.WriteAllText(_root.Dir("blocker"), "x");
        _folder.Folder = Path.Combine(_root.Dir("blocker"), "notes");

        var result = await AddTool().ExecuteAsync("""{"content":"x"}""", At(Now), default);

        Assert.True(result.IsError);
        Assert.DoesNotContain(_root.Path, result.Content);
    }

    [Fact]
    public void AddNote_Describe_ShowsTargetFileAndContentPreview()
    {
        var preview = AddTool().Preview("""{"content":"買牛奶和雞蛋","date":"2026-10-20"}""", At(Now));

        Assert.Equal("2026-10-20.md", preview.Target);
        Assert.Equal("買牛奶和雞蛋", preview.Detail);
    }

    [Fact]
    public void AddNote_Describe_DefaultsToToday_AndClipsLongContent()
    {
        var preview = AddTool().Preview(Json(new { content = new string('長', 200) }), At(Now));

        Assert.Equal("2026-10-23.md", preview.Target);
        Assert.Equal(new string('長', 80) + "…", preview.Detail);
    }

    [Fact]
    public void AddNote_Describe_BadDate_HasNoTarget()
    {
        var preview = AddTool().Preview("""{"content":"x","date":"bad"}""", At(Now));

        Assert.Null(preview.Target);
    }

    [Fact]
    public void AddNote_Descriptor_IsWriteBothModes_TenSeconds()
    {
        var descriptor = AddTool().Descriptor;

        Assert.Equal("add_note", descriptor.Name);
        Assert.Equal("記筆記", descriptor.DisplayName);
        Assert.Equal(ToolPermission.Write, descriptor.Permission);
        Assert.Equal(ToolModes.Both, descriptor.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), descriptor.Timeout);
        Assert.False(descriptor.RequiresScope);
    }

    // ---- read_notes ----

    [Fact]
    public async Task ReadNotes_NoArguments_ReadsToday_Only()
    {
        await _store.AppendAsync(new DateOnly(2026, 10, 22), new TimeOnly(8, 0), null, "昨天", default);
        await _store.AppendAsync(Day(), new TimeOnly(9, 5), "開發", "今天", default);

        var result = await ReadTool().ExecuteAsync("{}", At(Now), default);

        Assert.False(result.IsError);
        Assert.Equal("## 2026-10-23\n- 09:05 [開發] 今天", result.Content);
    }

    [Fact]
    public async Task ReadNotes_Range_ListsOnlyDaysWithNotes_AndIsInclusive()
    {
        await _store.AppendAsync(new DateOnly(2026, 10, 20), new TimeOnly(8, 0), null, "第一天", default);
        await _store.AppendAsync(new DateOnly(2026, 10, 22), new TimeOnly(8, 0), null, "第三天", default);
        await _store.AppendAsync(new DateOnly(2026, 10, 23), new TimeOnly(9, 0), null, "第四天", default);
        await _store.AppendAsync(new DateOnly(2026, 10, 24), new TimeOnly(9, 0), null, "範圍外", default);

        var result = await ReadTool().ExecuteAsync("""{"date_from":"2026-10-20","date_to":"2026-10-23"}""", At(Now), default);

        Assert.Equal("## 2026-10-20\n- 08:00 第一天\n\n## 2026-10-22\n- 08:00 第三天\n\n## 2026-10-23\n- 09:00 第四天", result.Content);
        Assert.Equal("3 則筆記，3 天", result.Summary);
    }

    [Fact]
    public async Task ReadNotes_MultiLineEntry_IsShownIndented()
    {
        await _store.AppendAsync(Day(), new TimeOnly(8, 0), null, "第一行\n第二行", default);

        var result = await ReadTool().ExecuteAsync("{}", At(Now), default);

        Assert.Equal("## 2026-10-23\n- 08:00 第一行\n  第二行", result.Content);
    }

    [Fact]
    public async Task ReadNotes_DateToDefaultsToDateFrom()
    {
        await _store.AppendAsync(new DateOnly(2026, 10, 20), new TimeOnly(8, 0), null, "那天", default);

        var result = await ReadTool().ExecuteAsync("""{"date_from":"2026-10-20"}""", At(Now), default);

        Assert.Equal("## 2026-10-20\n- 08:00 那天", result.Content);
    }

    [Fact]
    public async Task ReadNotes_Keyword_FiltersEntries()
    {
        await _store.AppendAsync(Day(), new TimeOnly(8, 0), null, "買牛奶", default);
        await _store.AppendAsync(Day(), new TimeOnly(9, 0), null, "寫報告", default);

        var result = await ReadTool().ExecuteAsync("""{"keyword":"報告"}""", At(Now), default);

        Assert.Equal("## 2026-10-23\n- 09:00 寫報告", result.Content);
    }

    [Fact]
    public async Task ReadNotes_NothingFound_SaysSo()
    {
        var result = await ReadTool().ExecuteAsync("""{"keyword":"不存在"}""", At(Now), default);

        Assert.False(result.IsError);
        Assert.Equal("指定範圍內沒有符合的筆記。", result.Content);
    }

    [Fact]
    public async Task ReadNotes_RangeOf31Days_IsOk_32Days_IsError()
    {
        var ok = await ReadTool().ExecuteAsync("""{"date_from":"2026-09-23","date_to":"2026-10-23"}""", At(Now), default);
        Assert.False(ok.IsError);

        var tooLong = await ReadTool().ExecuteAsync("""{"date_from":"2026-09-22","date_to":"2026-10-23"}""", At(Now), default);
        Assert.True(tooLong.IsError);
        Assert.Equal("日期範圍最多 31 天，請縮小範圍。", tooLong.Content);
    }

    [Fact]
    public async Task ReadNotes_EndBeforeStart_IsError()
    {
        var result = await ReadTool().ExecuteAsync("""{"date_from":"2026-10-23","date_to":"2026-10-20"}""", At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal("date_to 不可早於 date_from。", result.Content);
    }

    [Theory]
    [InlineData("""{"date_from":"2026/10/20"}""", "date_from 格式必須是 yyyy-MM-dd（例如 2026-09-30）。")]
    [InlineData("""{"date_from":"2026-10-20","date_to":"20261023"}""", "date_to 格式必須是 yyyy-MM-dd（例如 2026-09-30）。")]
    public async Task ReadNotes_BadDateFormat_NamesTheParameter(string json, string message)
    {
        var result = await ReadTool().ExecuteAsync(json, At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal(message, result.Content);
    }

    [Fact]
    public async Task ReadNotes_KeywordOverLimit_IsError()
    {
        var result = await ReadTool().ExecuteAsync(Json(new { keyword = new string('字', 51) }), At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal("keyword 太長（上限 50 字）。", result.Content);
    }

    [Fact]
    public async Task ReadNotes_StoreFailure_IsErrorResult()
    {
        var result = await ReadTool(new FailingNoteStore("無法讀取筆記資料夾，測試")).ExecuteAsync("{}", At(Now), default);

        Assert.True(result.IsError);
        Assert.Equal("無法讀取筆記：無法讀取筆記資料夾，測試", result.Content);
    }

    [Fact]
    public void ReadNotes_Describe_ShowsTheRange()
    {
        var preview = ReadTool().Preview("""{"date_from":"2026-10-20","date_to":"2026-10-23"}""", At(Now));

        Assert.Equal("2026-10-20 至 2026-10-23", preview.Target);
    }

    [Fact]
    public void ReadNotes_Descriptor_IsReadOnlyBothModes_TenSeconds()
    {
        var descriptor = ReadTool().Descriptor;

        Assert.Equal("read_notes", descriptor.Name);
        Assert.Equal("查筆記", descriptor.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, descriptor.Permission);
        Assert.Equal(ToolModes.Both, descriptor.Modes);
        Assert.Equal(TimeSpan.FromSeconds(10), descriptor.Timeout);
    }
}
