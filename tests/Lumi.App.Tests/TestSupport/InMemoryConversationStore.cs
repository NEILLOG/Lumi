using Lumi.Core.Chat;

namespace Lumi.App.Tests.TestSupport;

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, Conversation> _conversations = [];
    private readonly Dictionary<string, List<ChatMessage>> _messages = [];
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

    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<Conversation>>(_conversations.Values
                .Where(c => _messages[c.Id].Count > 0)
                .OrderByDescending(c => c.UpdatedAt).Take(limit).ToList());
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
