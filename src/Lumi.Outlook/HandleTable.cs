using System.Globalization;

namespace Lumi.Outlook;

/// <summary>
/// 信件與行程的短代號（m1、e1…）對照表：每則對話各自編號，只存在記憶體，每則對話最多 500 個（超過丟最舊的）。執行緒安全。
/// </summary>
internal sealed class HandleTable
{
    private const int MaxPerConversation = 500;

    private readonly object _lock = new();
    private readonly Dictionary<string, ConversationHandles> _conversations = new(StringComparer.Ordinal);

    /// <summary>加入並回傳代號（prefix＋流水號）；同一則對話、同一個 EntryID 再加時回原本的代號。</summary>
    public string Add(string conversationId, char prefix, string entryId, string storeId)
    {
        lock (_lock)
        {
            var conversation = GetOrCreate(conversationId);
            if (conversation.ByEntry.TryGetValue((prefix, entryId), out var existing))
                return existing;

            conversation.Counters.TryGetValue(prefix, out var last);
            var next = last + 1;
            conversation.Counters[prefix] = next;
            var code = prefix + next.ToString(CultureInfo.InvariantCulture);

            var entry = new Entry(prefix, entryId, storeId);
            conversation.Handles[code] = entry;
            conversation.ByEntry[(prefix, entryId)] = code;
            conversation.Order.Enqueue(code);

            // 超過上限就丟掉最舊加入的那一個；被丟的代號之後查不到
            if (conversation.Order.Count > MaxPerConversation)
            {
                var oldestCode = conversation.Order.Dequeue();
                var oldest = conversation.Handles[oldestCode];
                conversation.Handles.Remove(oldestCode);
                conversation.ByEntry.Remove((oldest.Prefix, oldest.EntryId));
            }

            return code;
        }
    }

    public bool TryGet(string conversationId, string id, out (string EntryId, string StoreId) handle)
    {
        lock (_lock)
        {
            if (_conversations.TryGetValue(conversationId, out var conversation)
                && conversation.Handles.TryGetValue(id, out var entry))
            {
                handle = (entry.EntryId, entry.StoreId);
                return true;
            }

            handle = default;
            return false;
        }
    }

    private ConversationHandles GetOrCreate(string conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            conversation = new ConversationHandles();
            _conversations[conversationId] = conversation;
        }
        return conversation;
    }

    private sealed record Entry(char Prefix, string EntryId, string StoreId);

    /// <summary>單一對話的狀態；只在 <see cref="_lock"/> 之下存取。</summary>
    private sealed class ConversationHandles
    {
        public readonly Dictionary<char, int> Counters = new();
        public readonly Dictionary<string, Entry> Handles = new(StringComparer.Ordinal);
        public readonly Dictionary<(char Prefix, string EntryId), string> ByEntry = new();
        public readonly Queue<string> Order = new();
    }
}
