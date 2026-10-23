using Lumi.Core.Chat;
using Lumi.Core.Memory;
using Lumi.Core.Tools;
using Lumi.Tools.Memory;
using Lumi.Tools.Tests.Notes;

namespace Lumi.Tools.Tests.Memory;

/// <summary>M14 14C：<c>memory_delete</c>——成功、找不到、id 格式、記憶關閉時，以及確認卡片。</summary>
public sealed class MemoryDeleteToolTests
{
    // 2026-10-23 12:00（UTC）
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeMemoryStore _store = new(new MemoryOptions());
    private readonly FakeModelCatalog _catalog = new();

    private MemoryDeleteTool Tool() => new(_store, _catalog);

    private static ToolContext Context(ChatMode mode = ChatMode.Workbench) =>
        new("c1", mode, new ClockAt(Now), EmptyFileScope.Instance);

    [Fact]
    public async Task Existing_IsDeleted()
    {
        _store.Seed("舊的");
        _store.Seed("另一則");

        var result = await Tool().ExecuteAsync("""{"id":"m1"}""", Context(), default);

        Assert.False(result.IsError);
        Assert.Equal("已忘掉 m1。", result.Content);
        Assert.Equal("已忘掉 m1", result.Summary);
        Assert.Equal(["另一則"], _store.Items.Select(i => i.Content));
    }

    [Fact]
    public async Task NotFound_IsError()
    {
        var result = await Tool().ExecuteAsync("""{"id":"m7"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("找不到記憶 m7。", result.Content);
        Assert.Equal("找不到", result.Summary);
    }

    [Theory]
    [InlineData("m1")]
    [InlineData("M1")]
    [InlineData("1")]
    [InlineData(" m1 ")]
    public async Task IdFormats_AreAccepted(string id)
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync(System.Text.Json.JsonSerializer.Serialize(new { id }), Context(), default);

        Assert.False(result.IsError);
        Assert.Empty(_store.Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("m0")]
    [InlineData("-3")]
    [InlineData("mabc")]
    public async Task BadId_IsError_BeforeTouchingStore(string id)
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync(System.Text.Json.JsonSerializer.Serialize(new { id }), Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("id 無效，請用像 m3 的代號。", result.Content);
        Assert.Empty(_store.Calls);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task MissingId_IsErrorResult_FromArgumentParsing()
    {
        var result = await Tool().ExecuteAsync("{}", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
    }

    [Fact]
    public async Task MemoryDisabled_IsError_AndStoreIsNotCalled()
    {
        _store.Seed("舊的");
        var tool = new MemoryDeleteTool(_store, new FakeModelCatalog(memoryEnabled: false));

        var result = await tool.ExecuteAsync("""{"id":"m1"}""", Context(), default);

        Assert.True(result.IsError);
        Assert.Equal("長期記憶目前已關閉（使用者可以在 設定 › 記憶 重新開啟）。", result.Content);
        Assert.Empty(_store.Calls);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task SpriteMode_Works()
    {
        _store.Seed("舊的");

        var result = await Tool().ExecuteAsync("""{"id":"m1"}""", Context(ChatMode.Sprite), default);

        Assert.False(result.IsError);
    }

    [Fact]
    public async Task MalformedJson_IsErrorResult_NotException()
    {
        var result = await Tool().ExecuteAsync("{", Context(), default);

        Assert.True(result.IsError);
        Assert.StartsWith("參數不正確", result.Content);
    }

    // ---- 確認卡片 ----

    [Fact]
    public void Preview_ShowsContent_AndIrreversibleNote()
    {
        _store.Seed("偏好繁體中文", MemoryCategory.Preference);

        var preview = Tool().Preview("""{"id":"m1"}""", Context());

        Assert.Equal("m1 · 偏好繁體中文", preview.Target);
        Assert.Equal("忘掉之後不再送給模型供應商，無法復原", preview.Detail);
    }

    [Fact]
    public void Preview_LongContent_IsClipped()
    {
        _store.Seed(new string('長', 100));

        var preview = Tool().Preview("""{"id":"m1"}""", Context());

        Assert.Equal("m1 · " + new string('長', 60) + "…", preview.Target);
    }

    [Fact]
    public void Preview_UnknownId_ShowsOnlyId()
    {
        var preview = Tool().Preview("""{"id":"m4"}""", Context());

        Assert.Equal("m4", preview.Target);
        Assert.Equal("忘掉之後不再送給模型供應商，無法復原", preview.Detail);
    }

    [Fact]
    public void Preview_InvalidId_HasNoTarget()
    {
        var preview = Tool().Preview("""{"id":"abc"}""", Context());

        Assert.Null(preview.Target);
        Assert.Equal("忘掉之後不再送給模型供應商，無法復原", preview.Detail);
    }

    [Fact]
    public void Preview_OfMalformedJson_DoesNotThrow()
    {
        var preview = Tool().Preview("{", Context());

        Assert.Equal(ToolPreview.None, preview);
    }
}
