using Lumi.Core.Chat;
using Lumi.Core.Notes;
using Lumi.Core.Tools;
using Lumi.Tools.Notes;

namespace Lumi.Tools.Tests.Notes;

/// <summary>M10 10B：<c>conversation_search</c>——對話（排除目前這則）與近一年的筆記合併、排序、截斷與錯誤處理。</summary>
public sealed class ConversationSearchToolTests : IDisposable
{
    private const string NoResult = "找不到符合的對話摘要或筆記。請改用同義詞、較短的關鍵字或更具體的主題。";

    // 2026-10-23 12:00（UTC）
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly NoteTempRoot _root = new();
    private readonly FolderBox _folder;
    private readonly NoteOptions _options = new();
    private readonly NoteStore _notes;
    private readonly FakeSearchConversations _conversations = new();

    public ConversationSearchToolTests()
    {
        _folder = new FolderBox(_root.Dir("notes"));
        _notes = new NoteStore(_folder, _options);
    }

    public void Dispose() => _root.Dispose();

    private ConversationSearchTool Tool(INoteStore? notes = null, IConversationStore? conversations = null) =>
        new(conversations ?? _conversations, notes ?? _notes, _options);

    private static ToolContext Context(string conversationId = "current") =>
        new(conversationId, ChatMode.Workbench, new ClockAt(Now), EmptyFileScope.Instance);

    private static ConversationSearchHit Hit(string id, string title, DateTimeOffset updated, string snippet = "片段") =>
        new(FakeSearchConversations.Conv(id, title, updated), snippet);

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    [Fact]
    public async Task Results_ExcludeTheCurrentConversation()
    {
        _conversations.Hits.Add(Hit("current", "這一則", Now));
        _conversations.Hits.Add(Hit("older", "之前的對話", Now.AddDays(-1), "之前說過的內容"));

        var result = await Tool().ExecuteAsync("""{"query":"內容"}""", Context("current"), default);

        Assert.False(result.IsError);
        Assert.Equal("來源：對話；日期：2026-10-22；標題：之前的對話\n之前說過的內容\n---", result.Content);
        Assert.Equal("找到 1 筆", result.Summary);
    }

    [Fact]
    public async Task Notes_AreIncluded_WithDateAndCategoryTitle()
    {
        await _notes.AppendAsync(new DateOnly(2026, 10, 20), new TimeOnly(9, 30), "開發", "登入的 token 要更新", default);

        var result = await Tool().ExecuteAsync("""{"query":"token"}""", Context(), default);

        Assert.Equal("來源：筆記；日期：2026-10-20；標題：09:30 開發\n登入的 token 要更新\n---", result.Content);
    }

    [Fact]
    public async Task Merged_SortedByDateNewestFirst()
    {
        await _notes.AppendAsync(new DateOnly(2026, 10, 1), new TimeOnly(9, 0), null, "關鍵字 很舊", default);
        _conversations.Hits.Add(Hit("a", "新對話", Now.AddDays(-2), "關鍵字 新"));
        _conversations.Hits.Add(Hit("b", "舊對話", Now.AddDays(-20), "關鍵字 中"));

        var result = await Tool().ExecuteAsync("""{"query":"關鍵字"}""", Context(), default);

        var headers = result.Content.Split('\n').Where(l => l.StartsWith("來源：", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            ["來源：對話；日期：2026-10-21；標題：新對話", "來源：對話；日期：2026-10-03；標題：舊對話", "來源：筆記；日期：2026-10-01；標題：09:00"],
            headers);
    }

    [Fact]
    public async Task AtMostTen_Results()
    {
        for (var i = 0; i < 15; i++)
            _conversations.Hits.Add(Hit($"c{i}", $"對話 {i}", Now.AddHours(-i), $"內容 {i}"));

        var result = await Tool().ExecuteAsync("""{"query":"內容"}""", Context(), default);

        Assert.Equal(10, result.Content.Split("\n---").Length - 1);
    }

    [Fact]
    public async Task NoResults_SaysSo()
    {
        var result = await Tool().ExecuteAsync("""{"query":"完全沒有"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(NoResult, result.Content);
    }

    [Fact]
    public async Task TitleOnlyHit_ShowsPlaceholderInsteadOfEmptyBody()
    {
        _conversations.Hits.Add(Hit("a", "只有標題符合", Now, ""));

        var result = await Tool().ExecuteAsync("""{"query":"標題"}""", Context(), default);

        Assert.Equal("來源：對話；日期：2026-10-23；標題：只有標題符合\n（只有標題符合，沒有內容片段）\n---", result.Content);
    }

    [Fact]
    public async Task LongSnippet_IsClippedTo1000Characters()
    {
        _conversations.Hits.Add(Hit("a", "長", Now, new string('長', 1500)));

        var result = await Tool().ExecuteAsync("""{"query":"長"}""", Context(), default);

        var body = result.Content.Split('\n')[1];
        Assert.Equal(new string('長', 1000) + "…", body);
    }

    [Fact]
    public async Task Query_IsTrimmed_AndPassedToTheConversationStore_WithAScanLimit()
    {
        await Tool().ExecuteAsync("""{"query":"  專案 計畫  "}""", Context(), default);

        Assert.Equal("專案 計畫", _conversations.LastQuery);
        Assert.Equal(_options.SearchConversationScanLimit, _conversations.LastLimit);
    }

    [Fact]
    public async Task EmptyQuery_IsError()
    {
        foreach (var json in new[] { """{"query":""}""", """{"query":"   "}""" })
        {
            var result = await Tool().ExecuteAsync(json, Context(), default);
            Assert.True(result.IsError);
            Assert.Equal("請提供要搜尋的主題或關鍵字。", result.Content);
        }

        // 缺少 query 是必填參數：由工具基底回參數錯誤
        var missing = await Tool().ExecuteAsync("{}", Context(), default);
        Assert.True(missing.IsError);
        Assert.StartsWith("參數不正確", missing.Content);
    }

    [Fact]
    public async Task QueryOver100Characters_IsError_At100_IsOk()
    {
        var ok = await Tool().ExecuteAsync(Json(new { query = new string('字', 100) }), Context(), default);
        Assert.False(ok.IsError);

        var over = await Tool().ExecuteAsync(Json(new { query = new string('字', 101) }), Context(), default);
        Assert.True(over.IsError);
        Assert.Equal("搜尋字串過長（上限 100 字）。", over.Content);
    }

    [Fact]
    public async Task NotesOlderThanAYear_AreNotSearched()
    {
        // 範圍是含今天在內的 365 天：今天往前 364 天（2025-10-24）還在範圍內，再早一天（2025-10-23）不算
        var first = Now.Date.AddDays(-364);
        await _notes.AppendAsync(DateOnly.FromDateTime(first), new TimeOnly(8, 0), null, "一年內", default);
        await _notes.AppendAsync(DateOnly.FromDateTime(first.AddDays(-1)), new TimeOnly(8, 0), null, "一年外", default);

        var result = await Tool().ExecuteAsync("""{"query":"一年"}""", Context(), default);

        Assert.Contains("一年內", result.Content);
        Assert.DoesNotContain("一年外", result.Content);
    }

    [Fact]
    public async Task ConversationStoreFailure_StillReturnsNotes()
    {
        await _notes.AppendAsync(new DateOnly(2026, 10, 20), new TimeOnly(9, 30), null, "筆記還在", default);
        _conversations.Failure = new InvalidOperationException("資料庫忙碌");

        var result = await Tool().ExecuteAsync("""{"query":"還在"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Contains("來源：筆記；日期：2026-10-20", result.Content);
    }

    [Fact]
    public async Task NoteStoreFailure_StillReturnsConversations()
    {
        _conversations.Hits.Add(Hit("a", "對話還在", Now.AddDays(-1), "還在"));

        var result = await Tool(notes: new FailingNoteStore("失敗")).ExecuteAsync("""{"query":"還在"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("來源：對話；日期：2026-10-22；標題：對話還在\n還在\n---", result.Content);
    }

    [Fact]
    public async Task BothSourcesFailing_IsError()
    {
        _conversations.Failure = new InvalidOperationException("失敗");

        var result = await Tool(notes: new FailingNoteStore("失敗")).ExecuteAsync("""{"query":"x"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("無法搜尋對話與筆記。", result.Content);
    }

    [Fact]
    public void Describe_ShowsTheQuery_AndDescriptorIsReadOnlyBothModes_FifteenSeconds()
    {
        var tool = Tool();

        Assert.Equal("專案", tool.Preview("""{"query":" 專案 "}""", Context()).Target);
        Assert.Equal("conversation_search", tool.Descriptor.Name);
        Assert.Equal("搜尋過去對話", tool.Descriptor.DisplayName);
        Assert.Equal(ToolPermission.ReadOnly, tool.Descriptor.Permission);
        Assert.Equal(ToolModes.Both, tool.Descriptor.Modes);
        Assert.Equal(TimeSpan.FromSeconds(15), tool.Descriptor.Timeout);
    }
}
