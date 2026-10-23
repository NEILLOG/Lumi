using Lumi.Core.Chat;
using Lumi.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests;

/// <summary>搜尋：標題加訊息文字，LIKE 跳脫，1～2 字中文，片段擷取。</summary>
public sealed class SqliteConversationStoreSearchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-search-tests-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "nested", "lumi.db");

    private SqliteConversationStore NewStore() =>
        new(Options.Create(new PersistenceOptions { DatabasePath = DatabasePath }));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ChatMessage Message(ChatRole role, params ContentBlock[] content) =>
        new(ChatMessage.NewId(), role, content, DateTimeOffset.UtcNow);

    /// <summary>建一則對話並加入一則使用者訊息（文字）。</summary>
    private static async Task<Conversation> WithText(SqliteConversationStore store, string title, string text)
    {
        var conversation = await store.CreateAsync(title);
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.User, new TextBlock(text)));
        return conversation;
    }

    [Fact]
    public async Task Search_ChineseWords_MatchMessageText_WithSnippet()
    {
        var store = NewStore();
        var conversation = await WithText(store, "週報", "這週的報告已經寫好了，明天交給主管");

        var hit = Assert.Single(await store.SearchAsync("報告", 10));

        Assert.Equal(conversation.Id, hit.Conversation.Id);
        Assert.Contains("報告", hit.Snippet);
        Assert.Equal("這週的報告已經寫好了，明天交給主管", hit.Snippet);
    }

    [Fact]
    public async Task Search_OneAndTwoCharacterChinese_Match()
    {
        var store = NewStore();
        await WithText(store, "A", "今天去台北開會");

        Assert.Single(await store.SearchAsync("北", 10));
        Assert.Single(await store.SearchAsync("台北", 10));
        Assert.Single(await store.SearchAsync("開會", 10));
    }

    [Fact]
    public async Task Search_English_IsCaseInsensitive()
    {
        var store = NewStore();
        await WithText(store, "english", "Hello World, the REPORT is ready");

        var hit = Assert.Single(await store.SearchAsync("report", 10));
        Assert.Equal("Hello World, the REPORT is ready", hit.Snippet);
        Assert.Single(await store.SearchAsync("HELLO", 10));
    }

    [Fact]
    public async Task Search_MatchesTitle_WithEmptySnippet_WhenNoMessageMatches()
    {
        var store = NewStore();
        await WithText(store, "旅遊計畫", "沒有提到那個字");

        var hit = Assert.Single(await store.SearchAsync("旅遊", 10));

        Assert.Equal("旅遊計畫", hit.Conversation.Title);
        Assert.Equal(string.Empty, hit.Snippet);
    }

    [Fact]
    public async Task Search_PrefersMessageSnippet_WhenTitleAlsoMatches()
    {
        var store = NewStore();
        await WithText(store, "報告草稿", "這是報告的內容");

        var hit = Assert.Single(await store.SearchAsync("報告", 10));

        Assert.Equal("這是報告的內容", hit.Snippet);
    }

    [Fact]
    public async Task Search_Percent_IsLiteral()
    {
        var store = NewStore();
        var exact = await WithText(store, "進度", "目前 100% 完成");
        await WithText(store, "另一個", "目前 100x 完成");

        var hit = Assert.Single(await store.SearchAsync("100%", 10));

        Assert.Equal(exact.Id, hit.Conversation.Id);
        // 單獨的 % 只命中含有 % 字元的那則，不是全部
        Assert.Equal(exact.Id, Assert.Single(await store.SearchAsync("%", 10)).Conversation.Id);
    }

    [Fact]
    public async Task Search_Underscore_IsLiteral()
    {
        var store = NewStore();
        var exact = await WithText(store, "變數", "設定 a_b 的值");
        await WithText(store, "別的", "設定 axb 的值");

        var hit = Assert.Single(await store.SearchAsync("a_b", 10));

        Assert.Equal(exact.Id, hit.Conversation.Id);
    }

    [Fact]
    public async Task Search_Backslash_IsLiteral()
    {
        var store = NewStore();
        var exact = await WithText(store, "路徑", @"放在 C:\temp 底下");
        await WithText(store, "另一個", "放在 C:temp 底下");

        var hit = Assert.Single(await store.SearchAsync(@"\", 10));

        Assert.Equal(exact.Id, hit.Conversation.Id);
    }

    [Fact]
    public async Task Search_OnlyWildcards_DoNotMatchEverything()
    {
        var store = NewStore();
        await WithText(store, "普通", "沒有特殊符號");

        Assert.Empty(await store.SearchAsync("%", 10));
        Assert.Empty(await store.SearchAsync("_", 10));
    }

    [Fact]
    public async Task Search_TrimsWhitespace_AndBlankQueryReturnsNothing()
    {
        var store = NewStore();
        await WithText(store, "A", "報告");

        Assert.Single(await store.SearchAsync("  報告  ", 10));
        Assert.Empty(await store.SearchAsync("   ", 10));
        Assert.Empty(await store.SearchAsync("", 10));
    }

    [Fact]
    public async Task Search_ResultsAreNewestFirst_AndRespectTheLimit()
    {
        var store = NewStore();
        var first = await WithText(store, "一", "關鍵字");
        await Task.Delay(5);
        var second = await WithText(store, "二", "關鍵字");
        await Task.Delay(5);
        var third = await WithText(store, "三", "關鍵字");

        Assert.Equal([third.Id, second.Id, first.Id], (await store.SearchAsync("關鍵字", 10)).Select(h => h.Conversation.Id));
        Assert.Equal([third.Id, second.Id], (await store.SearchAsync("關鍵字", 2)).Select(h => h.Conversation.Id));
    }

    [Fact]
    public async Task Search_SnippetKeepsAboutTwentyCharactersOnEachSide()
    {
        var store = NewStore();
        var text = new string('甲', 50) + "目標字" + new string('乙', 50);
        await WithText(store, "長文", text);

        var hit = Assert.Single(await store.SearchAsync("目標字", 10));

        Assert.Equal(new string('甲', 20) + "目標字" + new string('乙', 20), hit.Snippet);
    }

    [Fact]
    public async Task Search_ToolResultsAndContextAreNotSearched()
    {
        var store = NewStore();
        var conversation = await store.CreateAsync("工具");
        await store.AppendMessageAsync(conversation.Id, Message(ChatRole.Tool,
            new ToolResultBlock("x", "神秘工具輸出", false, "計算", null, null, 0, ToolApproval.NotRequired)));

        Assert.Empty(await store.SearchAsync("神秘", 10));
    }

    [Fact]
    public async Task Search_EmptyConversations_AreNotListed()
    {
        var store = NewStore();
        await store.CreateAsync("空的報告");

        Assert.Empty(await store.SearchAsync("報告", 10));
    }
}
