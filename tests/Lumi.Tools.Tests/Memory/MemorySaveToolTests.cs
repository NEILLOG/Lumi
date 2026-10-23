using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Tools;
using Lumi.Tools.Memory;
using Lumi.Tools.Tests.Notes;

namespace Lumi.Tools.Tests.Memory;

/// <summary>M14 14C：<c>memory_save</c>——參數驗證、來源由程式判定、重複與容量、記憶關閉時不寫入。</summary>
public sealed class MemorySaveToolTests
{
    // 2026-10-23 12:00（UTC）
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly MemoryOptions _options = new();
    private readonly FakeMemoryStore _store;
    private readonly FakeModelCatalog _catalog = new();
    private readonly FakeChatHistory _history = new();

    public MemorySaveToolTests()
    {
        _store = new FakeMemoryStore(_options);
    }

    private MemorySaveTool Tool() => new(_store, _catalog, _history, _options);

    private static ToolContext Context(ChatMode mode = ChatMode.Workbench, string conversationId = "c1") =>
        new(conversationId, mode, new ClockAt(Now), EmptyFileScope.Instance);

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    // ---- 成功與來源判定 ----

    [Fact]
    public async Task ExplicitRequest_IsSavedAsExplicit()
    {
        _history.Messages.Add(FakeChatHistory.User("請記住我偏好繁體中文"));

        var result = await Tool().ExecuteAsync("""{"content":"偏好繁體中文","category":"preference"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已記住 m1：偏好繁體中文（偏好）。", result.Content);
        Assert.Equal("已記住 m1", result.Summary);
        Assert.Equal(MemorySource.Explicit, _store.LastAdd!.Source);
        Assert.Equal("c1", _store.LastAdd.SourceConversationId);
        Assert.Equal(MemoryCategory.Preference, _store.LastAdd.Category);
        Assert.Null(_store.LastAdd.ExpiresAt);
    }

    [Fact]
    public async Task NoTriggerWord_IsSavedAsSuggested()
    {
        _history.Messages.Add(FakeChatHistory.User("我是後端工程師，在做內部工具"));

        var result = await Tool().ExecuteAsync("""{"content":"後端工程師","category":"work"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(MemorySource.Suggested, _store.LastAdd!.Source);
    }

    [Fact]
    public async Task ModelClaimingExplicit_IsIgnored()
    {
        // 模型在引數裡塞 source 也沒有用：參數沒有 source，多出來的欄位會被忽略，來源仍由程式依訊息判定
        _history.Messages.Add(FakeChatHistory.User("我是後端工程師"));

        var result = await Tool().ExecuteAsync(
            """{"content":"後端工程師","category":"work","source":"explicit"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(MemorySource.Suggested, _store.LastAdd!.Source);
    }

    [Theory]
    [InlineData("Please remember I prefer dark mode")]
    [InlineData("REMEMBER this about me")]
    [InlineData("forget nothing, remember: I use Linux")]
    public async Task EnglishTriggerWords_AreExplicit_CaseInsensitive(string userText)
    {
        _history.Messages.Add(FakeChatHistory.User(userText));

        await Tool().ExecuteAsync("""{"content":"喜歡深色模式","category":"preference"}""", Context(), default);

        Assert.Equal(MemorySource.Explicit, _store.LastAdd!.Source);
    }

    [Fact]
    public async Task LatestUserMessage_SkipsToolResults_AndFindsTheRealUserMessage()
    {
        // 最後一則是工具結果（不是使用者說的話），要往前找真正的使用者訊息
        _history.Messages.Add(FakeChatHistory.User("記得我是老師"));
        _history.Messages.Add(FakeChatHistory.Assistant("好的"));
        _history.Messages.Add(FakeChatHistory.ToolResult("已記住 m1"));

        await Tool().ExecuteAsync("""{"content":"職業是老師","category":"work"}""", Context(), default);

        Assert.Equal(MemorySource.Explicit, _store.LastAdd!.Source);
    }

    [Fact]
    public async Task LatestUserMessage_WithoutTriggerWord_IsSuggested_EvenIfEarlierOneHadIt()
    {
        _history.Messages.Add(FakeChatHistory.User("記住我喜歡咖啡"));
        _history.Messages.Add(FakeChatHistory.Assistant("好"));
        _history.Messages.Add(FakeChatHistory.User("今天天氣如何"));

        await Tool().ExecuteAsync("""{"content":"喜歡咖啡","category":"preference"}""", Context(), default);

        Assert.Equal(MemorySource.Suggested, _store.LastAdd!.Source);
    }

    [Fact]
    public async Task UnreadableHistory_IsSuggested_AndStillSaves()
    {
        _history.Failure = new IOException("資料庫忙碌");

        var result = await Tool().ExecuteAsync("""{"content":"喜歡咖啡","category":"preference"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(MemorySource.Suggested, _store.LastAdd!.Source);
    }

    [Fact]
    public async Task SpriteMode_Works()
    {
        _history.Messages.Add(FakeChatHistory.User("記住我下午不吃辣"));

        var result = await Tool().ExecuteAsync(
            """{"content":"下午不吃辣","category":"preference"}""", Context(ChatMode.Sprite), default);

        Assert.False(result.IsError);
        Assert.Equal(MemorySource.Explicit, _store.LastAdd!.Source);
    }

    [Fact]
    public async Task CategoryAndExpiry_AreParsed()
    {
        var result = await Tool().ExecuteAsync(
            """{"content":"正在做 Lumi 專案","category":"PROJECT","expires_at":"2026-12-31"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(MemoryCategory.Project, _store.LastAdd!.Category);
        Assert.Equal(new DateOnly(2026, 12, 31), _store.LastAdd.ExpiresAt);
        Assert.Equal("已記住 m1：正在做 Lumi 專案（專案）。", result.Content);
    }

    [Fact]
    public async Task ExpiryToday_IsAccepted()
    {
        var result = await Tool().ExecuteAsync(
            """{"content":"今天在出差","category":"work","expires_at":"2026-10-23"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(new DateOnly(2026, 10, 23), _store.LastAdd!.ExpiresAt);
    }

    [Theory]
    [InlineData("profile", MemoryCategory.Profile)]
    [InlineData("Preference", MemoryCategory.Preference)]
    [InlineData("  work  ", MemoryCategory.Work)]
    public async Task Category_IsCaseInsensitive(string category, MemoryCategory expected)
    {
        var result = await Tool().ExecuteAsync(Json(new { content = "某事", category }), Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(expected, _store.LastAdd!.Category);
    }

    [Fact]
    public async Task Content_IsNormalized_BeforeSaving()
    {
        var result = await Tool().ExecuteAsync(
            Json(new { content = "  偏好\n\n 繁體中文  ", category = "preference" }), Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("偏好 繁體中文", _store.LastAdd!.Content);
        Assert.Equal("已記住 m1：偏好 繁體中文（偏好）。", result.Content);
    }

    // ---- 重複、容量、store 的拒絕 ----

    [Fact]
    public async Task Duplicate_IsNotAnError()
    {
        _store.Seed("偏好繁體中文", MemoryCategory.Preference);

        var result = await Tool().ExecuteAsync(
            """{"content":"  偏好繁體中文 ","category":"preference"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已經有相同的記憶 m1，不需要重複記錄。", result.Content);
        Assert.Equal("已有相同記憶", result.Summary);
    }

    [Fact]
    public async Task Duplicate_IsCaseInsensitive()
    {
        _store.Seed("I use Linux", MemoryCategory.Work);

        var result = await Tool().ExecuteAsync("""{"content":"i USE linux","category":"work"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Contains("m1", result.Content);
    }

    [Fact]
    public async Task TooManyItems_IsError_WithCountsAndLimits()
    {
        for (var i = 0; i < 100; i++)
            _store.Seed($"第 {i} 則", MemoryCategory.Project);

        var result = await Tool().ExecuteAsync("""{"content":"新的一則","category":"work"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal(
            "長期記憶已滿（目前 100 則、" + Chars(_store.Items) + " 字；上限 100 則、2,000 字）。"
            + "請告訴使用者，由使用者決定要刪除或合併哪些記憶後再重試。",
            result.Content);
        Assert.Equal("記憶已滿", result.Summary);
    }

    [Fact]
    public async Task TooManyCharacters_IsError_WithThousandsSeparators()
    {
        // 10 則各 190 字 = 1,900 字，再加 150 字就超過 2,000
        for (var i = 0; i < 10; i++)
            _store.Seed(new string('字', 190), MemoryCategory.Project);

        var result = await Tool().ExecuteAsync(
            Json(new { content = new string('新', 150), category = "work" }), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal(
            "長期記憶已滿（目前 10 則、1,900 字；上限 100 則、2,000 字）。"
            + "請告訴使用者，由使用者決定要刪除或合併哪些記憶後再重試。",
            result.Content);
    }

    [Fact]
    public async Task StoreInvalid_IsError_WithStoreMessage()
    {
        var result = await Tool().ExecuteAsync(
            """{"content":"sk-abcdefghijklmnop1234","category":"work"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Contains("金鑰", result.Content);
        Assert.Equal("無法記住", result.Summary);
    }

    [Fact]
    public async Task TooLongContent_IsRejectedByStore_WithLengthMessage()
    {
        var result = await Tool().ExecuteAsync(
            Json(new { content = new string('長', 201), category = "work" }), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("這則記憶太長（201 字，上限 200 字），請縮成一句話。", result.Content);
    }

    [Fact]
    public async Task EmptyContent_IsError_BeforeTouchingStore()
    {
        var result = await Tool().ExecuteAsync("""{"content":"   \n  ","category":"work"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("content 不能為空。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task MissingContent_IsErrorResult_FromArgumentParsing()
    {
        var result = await Tool().ExecuteAsync("""{"category":"work"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task StoreTooManyItems_FromStore_IsErrorWithCounts()
    {
        _store.NextAddResult = new MemoryAddResult(MemoryAddOutcome.TooManyItems, null, null);

        var result = await Tool().ExecuteAsync("""{"content":"某事","category":"work"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("長期記憶已滿（目前 0 則、0 字；上限 100 則、2,000 字）。", result.Content);
    }

    // ---- 參數錯誤（都不寫入） ----

    [Fact]
    public async Task MissingCategory_IsErrorResult_FromArgumentParsing()
    {
        // category 是必填的非可空欄位：缺少時在 ToolBase 解析引數就回錯誤，不會進到工具
        var result = await Tool().ExecuteAsync("""{"content":"某事"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Theory]
    [InlineData("""{"content":"某事","category":""}""")]
    [InlineData("""{"content":"某事","category":"food"}""")]
    [InlineData("""{"content":"某事","category":"1"}""")]
    public async Task BadCategory_IsError_BeforeTouchingStore(string json)
    {
        var result = await Tool().ExecuteAsync(json, Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("category 必須是 profile、preference、work 或 project。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Theory]
    [InlineData("2026/12/31")]
    [InlineData("2026-12-1")]
    [InlineData("2026-13-01")]
    [InlineData("明年")]
    public async Task BadExpiryFormat_IsError(string expiresAt)
    {
        var result = await Tool().ExecuteAsync(
            Json(new { content = "某事", category = "work", expires_at = expiresAt }), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("expires_at 必須是 YYYY-MM-DD 格式。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task ExpiryInThePast_IsError()
    {
        var result = await Tool().ExecuteAsync(
            """{"content":"某事","category":"work","expires_at":"2026-10-22"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("到期日不能早於今天。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task MalformedJson_IsErrorResult_NotException()
    {
        var result = await Tool().ExecuteAsync("{", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
        Assert.Empty(_store.Calls);
    }

    // ---- 記憶關閉 ----

    [Fact]
    public async Task MemoryDisabled_IsError_AndStoreIsNotCalled()
    {
        var catalog = new FakeModelCatalog(memoryEnabled: false);
        var tool = new MemorySaveTool(_store, catalog, _history, _options);

        var result = await tool.ExecuteAsync("""{"content":"喜歡咖啡","category":"preference"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("長期記憶目前已關閉（使用者可以在 設定 › 記憶 重新開啟）。", result.Content);
        Assert.Empty(_store.Calls);
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_ShowsCategory_AndProviderNote()
    {
        var preview = Tool().Preview("""{"content":"偏好繁體中文","category":"preference"}""", Context());

        Assert.Equal("偏好繁體中文", preview.Target);
        Assert.Equal("分類：偏好 · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_WithExpiry_ShowsValidUntil()
    {
        var preview = Tool().Preview(
            """{"content":"正在做 Lumi 專案","category":"project","expires_at":"2026-12-31"}""", Context());

        Assert.Equal("分類：專案 · 有效至 2026-12-31 · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_NormalizesAndClipsLongContent()
    {
        var content = "第一行\n\n" + new string('長', 70);

        var preview = Tool().Preview(Json(new { content, category = "work" }), Context());

        // 正規化後是「第一行 」（4 字）加 70 字 = 74 字，截到 60 字再加「…」
        Assert.Equal("第一行 " + new string('長', 56) + "…", preview.Target);
    }

    [Fact]
    public void Preview_OfMalformedJson_IsNone()
    {
        var preview = Tool().Preview("{", Context());

        Assert.Null(preview.Target);
        Assert.Null(preview.Detail);
    }

    private static string Chars(IEnumerable<MemoryItem> items) =>
        items.Sum(i => MemoryRules.Length(i.Content)).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
