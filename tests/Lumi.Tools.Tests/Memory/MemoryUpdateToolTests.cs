using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Tools;
using Lumi.Tools.Memory;
using Lumi.Tools.Tests.Notes;

namespace Lumi.Tools.Tests.Memory;

/// <summary>M14 14C：<c>memory_update</c>——欄位、id 格式、「never」、NotFound、重複、容量、確認卡片的「原本 → 之後」。</summary>
public sealed class MemoryUpdateToolTests
{
    // 2026-10-23 12:00（UTC）
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly MemoryOptions _options = new();
    private readonly FakeMemoryStore _store;
    private readonly FakeModelCatalog _catalog = new();

    public MemoryUpdateToolTests()
    {
        _store = new FakeMemoryStore(_options);
    }

    private MemoryUpdateTool Tool() => new(_store, _catalog, _options);

    private static ToolContext Context(ChatMode mode = ChatMode.Workbench) =>
        new("c1", mode, new ClockAt(Now), EmptyFileScope.Instance);

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    // ---- 成功 ----

    [Fact]
    public async Task Content_IsUpdated_OtherFieldsKept()
    {
        _store.Seed("舊內容", MemoryCategory.Preference, new DateOnly(2026, 12, 31));

        var result = await Tool().ExecuteAsync("""{"id":"m1","content":"新內容"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已更新 m1：新內容（偏好）。", result.Content);
        Assert.Equal("已更新 m1", result.Summary);
        Assert.Equal("新內容", _store.Items.Single().Content);
        Assert.Equal(MemoryCategory.Preference, _store.Items.Single().Category);
        Assert.Equal(new DateOnly(2026, 12, 31), _store.Items.Single().ExpiresAt);
        Assert.Equal("新內容", _store.LastPatch!.Content);
        Assert.Null(_store.LastPatch.Category);
        Assert.Null(_store.LastPatch.ExpiresAt);
        Assert.False(_store.LastPatch.ClearExpiresAt);
        Assert.False(_store.LastPatch.SkipSecretCheck);
    }

    [Fact]
    public async Task Category_IsUpdated()
    {
        _store.Seed("在一家新創公司工作", MemoryCategory.Profile);
        _store.Seed("x"); // 第 2 則，確保 id 是 m1 而不是剛好等於 m2

        var result = await Tool().ExecuteAsync("""{"id":"m1","category":"work"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已更新 m1：在一家新創公司工作（工作）。", result.Content);
        Assert.Equal(MemoryCategory.Work, _store.Items.First(i => i.Id == 1).Category);
        Assert.Equal(MemoryCategory.Work, _store.LastPatch!.Category);
    }

    [Fact]
    public async Task ExpiresAt_IsParsed()
    {
        _store.Seed("正在出差");

        var result = await Tool().ExecuteAsync("""{"id":"m1","expires_at":"2026-12-31"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(new DateOnly(2026, 12, 31), _store.LastPatch!.ExpiresAt);
        Assert.False(_store.LastPatch.ClearExpiresAt);
    }

    [Theory]
    [InlineData("never")]
    [InlineData("NEVER")]
    [InlineData(" never ")]
    public async Task ExpiresAtNever_ClearsExpiry(string value)
    {
        _store.Seed("正在出差", expiresAt: new DateOnly(2026, 12, 31));

        var result = await Tool().ExecuteAsync(Json(new { id = "m1", expires_at = value }), Context(), default);

        Assert.False(result.IsError);
        Assert.True(_store.LastPatch!.ClearExpiresAt);
        Assert.Null(_store.LastPatch.ExpiresAt);
        Assert.Null(_store.Items.Single().ExpiresAt);
        Assert.Equal("已更新 m1：正在出差（本人）。", result.Content);
    }

    [Fact]
    public async Task ExpiredMemory_CanBeUpdated_ToExtendExpiry()
    {
        _store.Seed("已過期的出差", expiresAt: new DateOnly(2026, 1, 1));

        var result = await Tool().ExecuteAsync("""{"id":"m1","expires_at":"2027-01-01"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal(new DateOnly(2027, 1, 1), _store.Items.Single().ExpiresAt);
    }

    [Fact]
    public async Task AllFields_CanBeUpdatedTogether()
    {
        _store.Seed("舊的", MemoryCategory.Preference);

        var result = await Tool().ExecuteAsync(
            """{"id":"m1","content":"新的","category":"work","expires_at":"2026-11-30"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已更新 m1：新的（工作）。", result.Content);
        Assert.Equal(MemoryCategory.Work, _store.Items.Single().Category);
        Assert.Equal(new DateOnly(2026, 11, 30), _store.Items.Single().ExpiresAt);
    }

    [Fact]
    public async Task Content_IsNormalized_BeforeUpdating()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync(Json(new { id = "m1", content = " 新\n\n的  內容 " }), Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("新 的 內容", _store.Items.Single().Content);
    }

    [Fact]
    public async Task SpriteMode_Works()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync("""{"id":"m1","content":"新的"}""", Context(ChatMode.Sprite), default);

        Assert.False(result.IsError);
    }

    // ---- id 格式 ----

    [Theory]
    [InlineData("m1")]
    [InlineData("M1")]
    [InlineData("1")]
    [InlineData(" m1 ")]
    public async Task IdFormats_AreAccepted(string id)
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync(Json(new { id, content = "新的" }), Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("新的", _store.Items.Single().Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("m0")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("m")]
    [InlineData("mabc")]
    [InlineData("1.5")]
    [InlineData("記憶一")]
    public async Task BadId_IsError_BeforeTouchingStore(string id)
    {
        var result = await Tool().ExecuteAsync(Json(new { id, content = "新的" }), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("id 無效，請用像 m3 的代號。", result.Content);
        Assert.Equal("id 無效", result.Summary);
        Assert.Empty(_store.Calls);
    }

    // ---- 參數錯誤 ----

    [Fact]
    public async Task NoFieldsGiven_IsError()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync("""{"id":"m1"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("至少要提供 content、category、expires_at 其中一項。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task BadCategory_IsError()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync("""{"id":"m1","category":"food"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("category 必須是 profile、preference、work 或 project。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Theory]
    [InlineData("2026/12/31")]
    [InlineData("明年")]
    [InlineData("2026-02-30")]
    public async Task BadExpiresAt_IsError(string value)
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync(Json(new { id = "m1", expires_at = value }), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("expires_at 必須是 YYYY-MM-DD 格式，或 never（清除到期日）。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task ExpiresAtInThePast_IsError()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync("""{"id":"m1","expires_at":"2026-10-01"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("到期日不能早於今天。", result.Content);
        Assert.Empty(_store.Calls);
    }

    [Fact]
    public async Task EmptyContent_IsRejectedByStore()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync("""{"id":"m1","content":"   "}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("記憶的內容不可為空。", result.Content);
        Assert.Equal("無法修改", result.Summary);
    }

    [Fact]
    public async Task SecretLikeContent_IsRejectedByStore()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync(Json(new { id = "m1", content = "sk-abcdefghijklmnop1234" }), Context(), default);

        Assert.True(result.IsError);
        Assert.Contains("金鑰", result.Content);
        Assert.Equal("舊的", _store.Items.Single().Content);
    }

    [Fact]
    public async Task MalformedJson_IsErrorResult_NotException()
    {
        var result = await Tool().ExecuteAsync("{", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
    }

    // ---- 找不到、重複、容量 ----

    [Fact]
    public async Task NotFound_IsError()
    {
        var result = await Tool().ExecuteAsync("""{"id":"m5","content":"新的"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("找不到記憶 m5。", result.Content);
        Assert.Equal("找不到", result.Summary);
    }

    [Fact]
    public async Task Duplicate_IsError_NamingTheOtherMemory()
    {
        _store.Seed("偏好繁體中文");
        _store.Seed("偏好英文");

        var result = await Tool().ExecuteAsync("""{"id":"m2","content":"  偏好繁體中文 "}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("已經有內容相同的記憶 m1。", result.Content);
        Assert.Equal("偏好英文", _store.Items.First(i => i.Id == 2).Content);
    }

    [Fact]
    public async Task TooManyCharacters_IsError()
    {
        _options.MaxTotalLength = 100;
        _store.Seed(new string('a', 60));
        _store.Seed(new string('b', 30));

        var result = await Tool().ExecuteAsync(Json(new { id = "m2", content = new string('新', 50) }), Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("長期記憶已滿（目前 2 則、90 字；上限 100 則、100 字）。", result.Content);
        Assert.Equal("記憶已滿", result.Summary);
    }

    [Fact]
    public async Task StoreReturnsNotFound_WhenStoreSaysSo()
    {
        _store.NextUpdateResult = new MemoryUpdateResult(MemoryUpdateOutcome.NotFound, null, null);

        var result = await Tool().ExecuteAsync("""{"id":"m1","content":"新的"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("找不到記憶 m1。", result.Content);
    }

    // ---- 記憶關閉 ----

    [Fact]
    public async Task MemoryDisabled_IsError_AndStoreIsNotCalled()
    {
        _store.Seed("舊的");
        var tool = new MemoryUpdateTool(_store, new FakeModelCatalog(memoryEnabled: false), _options);

        var result = await tool.ExecuteAsync("""{"id":"m1","content":"新的"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("長期記憶目前已關閉（使用者可以在 設定 › 記憶 重新開啟）。", result.Content);
        Assert.Empty(_store.Calls);
        Assert.Equal("舊的", _store.Items.Single().Content);
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_ContentChange_ShowsBeforeAndAfter()
    {
        _store.Seed("舊內容", MemoryCategory.Preference);

        var preview = Tool().Preview("""{"id":"m1","content":"新內容","category":"work"}""", Context());

        Assert.Equal("m1 · 新內容", preview.Target);
        Assert.Equal("原本：「舊內容」（偏好） → 之後：「新內容」（工作） · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_CategoryOnly_TargetIsOriginalContent()
    {
        _store.Seed("舊內容", MemoryCategory.Preference);

        var preview = Tool().Preview("""{"id":"m1","category":"work"}""", Context());

        Assert.Equal("m1 · 舊內容", preview.Target);
        Assert.Equal("原本：（偏好） → 之後：（工作） · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_ExpiryChange_ShowsPermanentAndValidUntil()
    {
        _store.Seed("正在出差");

        var preview = Tool().Preview("""{"id":"m1","expires_at":"2026-12-31"}""", Context());

        Assert.Equal("原本：永久 → 之後：有效至 2026-12-31 · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_Never_ShowsClearingExpiry()
    {
        _store.Seed("正在出差", expiresAt: new DateOnly(2026, 12, 31));

        var preview = Tool().Preview("""{"id":"m1","expires_at":"never"}""", Context());

        Assert.Equal("原本：有效至 2026-12-31 → 之後：永久 · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_NothingActuallyChanged_SaysSo()
    {
        _store.Seed("偏好繁體中文", MemoryCategory.Preference);

        var preview = Tool().Preview("""{"id":"m1","content":"偏好繁體中文","category":"preference"}""", Context());

        Assert.Equal("沒有變更 · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_OriginalGone_ShowsOnlyAfter()
    {
        var preview = Tool().Preview("""{"id":"m9","content":"新內容","category":"work"}""", Context());

        Assert.Equal("m9 · 新內容", preview.Target);
        Assert.Equal("之後：「新內容」（工作） · 會隨每則訊息送到模型供應商", preview.Detail);
    }

    [Fact]
    public void Preview_InvalidId_HasNoTarget()
    {
        var preview = Tool().Preview("""{"id":"x","content":"新內容"}""", Context());

        Assert.Null(preview.Target);
    }

    [Fact]
    public void Preview_OfMalformedJson_IsNone()
    {
        var preview = Tool().Preview("{", Context());

        Assert.Null(preview.Target);
        Assert.Null(preview.Detail);
    }
}
