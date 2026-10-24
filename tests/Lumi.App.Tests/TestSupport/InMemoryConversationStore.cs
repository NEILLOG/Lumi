using Lumi.Core.Chat;

namespace Lumi.App.Tests.TestSupport;

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, Conversation> _conversations = [];
    private readonly Dictionary<string, List<ChatMessage>> _messages = [];
    private readonly Dictionary<string, string> _systemPrompts = [];
    private readonly object _lock = new();

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation(Conversation.NewId(), title, now, now);
        lock (_lock)
        {
            _conversations[conversation.Id] = conversation;
            _messages[conversation.Id] = [];
        }
        return Task.FromResult(conversation);
    }

    public Task<Conversation?> GetLatestAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_conversations.Values.OrderByDescending(c => c.UpdatedAt).FirstOrDefault());
    }

    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, CancellationToken ct = default) => ListAsync(limit, null, ct);

    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Conversation>>(_conversations.Values
                .Where(c => _messages[c.Id].Count > 0)
                .Where(c => before is null || c.UpdatedAt < before.UpdatedAt
                            || (c.UpdatedAt == before.UpdatedAt && string.CompareOrdinal(c.Id, before.Id) < 0))
                .OrderByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id, StringComparer.Ordinal)
                .Take(limit).ToList());
    }

    public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var existed = _conversations.Remove(conversationId);
            _messages.Remove(conversationId);
            return Task.FromResult(existed);
        }
    }

    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        var text = query.Trim();
        var hits = new List<ConversationSearchHit>();
        if (text.Length == 0 || limit <= 0)
            return Task.FromResult<IReadOnlyList<ConversationSearchHit>>(hits);

        lock (_lock)
        {
            foreach (var conversation in _conversations.Values.Where(c => _messages[c.Id].Count > 0).OrderByDescending(c => c.UpdatedAt))
            {
                var matched = _messages[conversation.Id]
                    .Select(m => SearchTextExtractor.Extract(m.Content))
                    .FirstOrDefault(t => t.Contains(text, StringComparison.OrdinalIgnoreCase));
                if (matched is null && !conversation.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits.Add(new ConversationSearchHit(conversation, matched is null ? "" : SearchSnippet.Create(matched, text)));
                if (hits.Count >= limit)
                    break;
            }
        }
        return Task.FromResult<IReadOnlyList<ConversationSearchHit>>(hits);
    }

    public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var list = _messages[conversationId];
            if (list.Count == 0)
                return Task.FromResult(false);
            list.RemoveAt(list.Count - 1);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
            return Task.FromResult<IReadOnlyList<ChatMessage>>(_messages[conversationId].ToList());
    }

    public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _messages[conversationId].Add(message);
            _conversations[conversationId] = _conversations[conversationId] with { UpdatedAt = DateTimeOffset.UtcNow };
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetSystemPromptAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_systemPrompts.GetValueOrDefault(conversationId));
    }

    public Task SetSystemPromptAsync(string conversationId, string systemPrompt, CancellationToken ct = default)
    {
        lock (_lock)
            _systemPrompts[conversationId] = systemPrompt;
        return Task.CompletedTask;
    }

    public Task SetTurnSuffixAsync(string conversationId, string messageId, string? turnSuffix, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var list = _messages[conversationId];
            var index = list.FindIndex(m => m.Id == messageId);
            if (index >= 0)
                list[index] = list[index] with { TurnSuffix = turnSuffix };
        }
        return Task.CompletedTask;
    }

    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default)
    {
        lock (_lock)
            _conversations[conversationId] = _conversations[conversationId] with { Title = title };
        return Task.CompletedTask;
    }

    public Conversation Get(string id)
    {
        lock (_lock)
            return _conversations[id];
    }
}
