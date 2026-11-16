using System.Text.Json;
using Lumi.Core.Chat;
using Lumi.Core.ContextNotes;
using Lumi.Core.Tools;
using Lumi.Tools.ContextNotes;

namespace Lumi.Tools.Tests.ContextNotes;

/// <summary>對話儲存的測試替身：只實作取得單一對話，其他方法丟 <see cref="NotImplementedException"/>。</summary>
internal sealed class SuggestionConversations : IConversationStore
{
    private readonly Dictionary<string, Conversation> _items = [];

    public Conversation Add(ConversationKind kind = ConversationKind.Chat)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(Conversation.NewId(), "測試對話", now, now, kind);
        _items[conversation.Id] = conversation;
        return conversation;
    }

    public Task<Conversation?> GetConversationAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(conversationId));

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => throw new NotImplementedException();
    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotImplementedException();
}

/// <summary><c>suggest_context_note</c>（M35 D9）：五種執行結果、建議紀錄，以及描述與預覽。</summary>
public sealed class SuggestContextNoteToolTests
{
    private readonly InMemoryContextNoteStore _notes = new();
    private readonly SuggestionConversations _conversations = new();
    private readonly SuggestContextNoteTool _tool;

    public SuggestContextNoteToolTests() => _tool = new SuggestContextNoteTool(_notes, _conversations);

    private static ToolContext Context(string conversationId) =>
        new(conversationId, ChatMode.Workbench, TimeProvider.System, EmptyFileScope.Instance);

    private static string Args(string goal) => JsonSerializer.Serialize(new { goal });

    private Task<ToolResult> Run(string conversationId, string goal) =>
        _tool.ExecuteAsync(Args(goal), Context(conversationId), default);

    // ---- 描述 ----

    [Fact]
    public void Descriptor_IsReadOnlyWorkbenchNotesWithFiveSecondTimeout()
    {
        var d = _tool.Descriptor;

        Assert.Equal("suggest_context_note", SuggestContextNoteTool.ToolName);
        Assert.Equal(SuggestContextNoteTool.ToolName, d.Name);
        Assert.Equal("建議開研究本", d.DisplayName);
        Assert.Equal("在適合的時候詢問你要不要把討論整理成研究本。", d.Summary);
        Assert.Equal(ToolPermission.ReadOnly, d.Permission);
        Assert.Equal(ToolModes.Workbench, d.Modes);
        Assert.Equal(ToolGroups.Notes, d.Group);
        Assert.Equal(TimeSpan.FromSeconds(5), d.Timeout);
        Assert.True(d.CanDisable);
    }

    [Fact]
    public void Describe_TargetIsTrimmedGoal()
    {
        var preview = _tool.Preview(Args("  比較 A 與 B 方案  "), Context("c1"));

        Assert.Equal("比較 A 與 B 方案", preview.Target);
        Assert.Null(preview.Detail);
    }

    // ---- 五種結果 ----

    [Fact]
    public async Task ScheduledRun_IsRejectedAndNothingIsRecorded()
    {
        var conversation = _conversations.Add(ConversationKind.ScheduledRun);

        var result = await Run(conversation.Id, "比較兩個方案");

        Assert.True(result.IsError);
        Assert.Equal("排程執行不能建立研究本。", result.Content);
        Assert.Equal("排程不能使用", result.Summary);
        Assert.Null(await _notes.GetSuggestionAsync(conversation.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyGoal_IsRejected(string goal)
    {
        var conversation = _conversations.Add();

        var result = await Run(conversation.Id, goal);

        Assert.True(result.IsError);
        Assert.Equal("目標要寫成 1 到 200 個字的一兩句話。", result.Content);
        Assert.Equal("目標不合格式", result.Summary);
        Assert.Null(await _notes.GetSuggestionAsync(conversation.Id));
    }

    [Fact]
    public async Task GoalOverLimit_IsRejected_ButExactLimitIsAccepted()
    {
        var conversation = _conversations.Add();

        var tooLong = await Run(conversation.Id, new string('字', ContextNoteLimits.GoalMaxLength + 1));
        Assert.True(tooLong.IsError);
        Assert.Equal("目標不合格式", tooLong.Summary);

        var exact = await Run(conversation.Id, new string('字', ContextNoteLimits.GoalMaxLength));
        Assert.False(exact.IsError);
    }

    [Fact]
    public async Task ConversationAlreadyHasNote_IsRejected()
    {
        var conversation = _conversations.Add();
        var now = DateTimeOffset.UtcNow;
        await _notes.CreateAsync(
            new ContextNote(ContextNote.NewId(), conversation.Id, "舊目標", false, 1, 0, null, null, now, now),
            new ContextNoteRevision(ContextNoteRevision.NewId(), "unused", 1, ContextNoteRevisionKind.StartConversation,
                "舊目標", "", [], null, 0, now));

        var result = await Run(conversation.Id, "比較兩個方案");

        Assert.True(result.IsError);
        Assert.Equal("這則對話已經有研究本，不要再建議。", result.Content);
        Assert.Equal("已經有研究本", result.Summary);
        Assert.Null(await _notes.GetSuggestionAsync(conversation.Id));
    }

    [Fact]
    public async Task Success_RecordsSuggestion_AndSecondCallSaysAlreadySuggested()
    {
        var conversation = _conversations.Add();

        var first = await Run(conversation.Id, "比較兩個方案");

        Assert.False(first.IsError);
        Assert.Equal("已經在畫面上詢問使用者。照常回答使用者的問題，不要再提這件事。", first.Content);
        Assert.Equal("比較兩個方案", first.Summary);
        var suggestion = await _notes.GetSuggestionAsync(conversation.Id);
        Assert.NotNull(suggestion);
        Assert.Equal(conversation.Id, suggestion.ConversationId);
        Assert.Null(suggestion.DismissedAt);

        var second = await Run(conversation.Id, "比較兩個方案");

        Assert.True(second.IsError);
        Assert.Equal("這則對話已經建議過，不要再建議。", second.Content);
        Assert.Equal("已經建議過", second.Summary);
    }

    [Fact]
    public async Task AlreadySuggestedRecord_IsRejected_WithoutChangingIt()
    {
        var conversation = _conversations.Add();
        var earlier = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        await _notes.TryMarkSuggestedAsync(conversation.Id, earlier);

        var result = await Run(conversation.Id, "比較兩個方案");

        Assert.True(result.IsError);
        Assert.Equal("已經建議過", result.Summary);
        Assert.Equal(earlier, (await _notes.GetSuggestionAsync(conversation.Id))!.SuggestedAt);
    }

    [Fact]
    public async Task MissingConversation_IsGenericError_NotAlreadySuggested()
    {
        var result = await Run("不存在的對話", "比較兩個方案");

        Assert.True(result.IsError);
        Assert.Equal("找不到這則對話，不能建議研究本。", result.Content);
        Assert.Equal("找不到對話", result.Summary);
        Assert.Null(await _notes.GetSuggestionAsync("不存在的對話"));
    }
}
