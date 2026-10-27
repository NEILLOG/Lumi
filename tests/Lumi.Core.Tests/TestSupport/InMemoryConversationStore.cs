using Lumi.Core.Chat;

namespace Lumi.Core.Tests.TestSupport;

internal sealed class InMemoryConversationStore : IConversationStore
{
    private readonly Dictionary<string, Conversation> _conversations = [];
    private readonly Dictionary<string, List<ChatMessage>> _messages = [];
    private readonly Dictionary<string, string> _systemPrompts = [];
    private readonly Dictionary<string, string> _toolSnapshots = [];
    private readonly Dictionary<string, List<(CompactionRecord Record, int AtSeq)>> _compactions = [];
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
            _compactions.Remove(conversationId);
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
            return Task.FromResult<IReadOnlyList<ChatMessage>>(_messages[conversationId].Select((m, i) => m with { Seq = i + 1 }).ToList());
    }

    public Task AddCompactionAsync(string conversationId, CompactionRecord record, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_compactions.TryGetValue(conversationId, out var list))
                _compactions[conversationId] = list = [];
            list.Add((record, _messages[conversationId].Count));
        }
        return Task.CompletedTask;
    }

    public Task<CompactionState> GetCompactionStateAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_compactions.TryGetValue(conversationId, out var list) || list.Count == 0)
                return Task.FromResult(CompactionState.Empty);
            var tool = list.LastOrDefault(r => r.Record.Kind == CompactionKind.ToolResults).Record;
            var summary = list.LastOrDefault(r => r.Record.Kind == CompactionKind.Summary).Record;
            return Task.FromResult(new CompactionState(tool?.UptoSeq, summary?.UptoSeq, summary?.Summary, list.Max(r => r.AtSeq)));
        }
    }

    public Task<TokenUsage> GetCompactionUsageAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var usages = _compactions.GetValueOrDefault(conversationId)?.Select(r => r.Record.Usage).OfType<TokenUsage>().ToList() ?? [];
            return Task.FromResult(new TokenUsage(
                usages.Sum(u => u.InputTokens), usages.Sum(u => u.OutputTokens),
                usages.Sum(u => u.CacheCreationInputTokens), usages.Sum(u => u.CacheReadInputTokens)));
        }
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

    public Task<string?> GetToolSnapshotAsync(string conversationId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_toolSnapshots.GetValueOrDefault(conversationId));
    }

    public Task SetToolSnapshotAsync(string conversationId, string snapshotJson, CancellationToken ct = default)
    {
        lock (_lock)
            _toolSnapshots[conversationId] = snapshotJson;
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

    public Task<TokenTotals> GetTokenTotalsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var usages = _messages.Values.SelectMany(list => list)
                .Where(m => m.Role == ChatRole.Assistant && m.Usage is not null && m.CreatedAt >= since)
                .Select(m => m.Usage!)
                .ToList();
            var input = usages.Sum(u => (long)u.InputTokens + u.CacheCreationInputTokens + u.CacheReadInputTokens);
            var cacheRead = usages.Sum(u => (long)u.CacheReadInputTokens);
            var output = usages.Sum(u => (long)u.OutputTokens);
            return Task.FromResult(new TokenTotals(input, cacheRead, output));
        }
    }

    public Task<IReadOnlyList<RecentToolAction>> ListRecentToolActionsAsync(int limit, CancellationToken ct = default)
    {
        var actions = new List<RecentToolAction>();
        if (limit <= 0)
            return Task.FromResult<IReadOnlyList<RecentToolAction>>(actions);

        lock (_lock)
        {
            var rows = _messages
                .SelectMany(pair => pair.Value.Where(m => m.Role == ChatRole.Tool).Select(m => (ConversationId: pair.Key, Message: m)))
                .OrderByDescending(row => row.Message.CreatedAt)
                .ToList();
            foreach (var row in rows)
            {
                foreach (var result in row.Message.Content.OfType<ToolResultBlock>().Reverse())
                {
                    if (actions.Count >= limit)
                        break;
                    actions.Add(new RecentToolAction(row.Message.CreatedAt, row.ConversationId, result));
                }
                if (actions.Count >= limit)
                    break;
            }
        }
        return Task.FromResult<IReadOnlyList<RecentToolAction>>(actions);
    }

    public Conversation Get(string id)
    {
        lock (_lock)
            return _conversations[id];
    }
}
