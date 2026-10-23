using Lumi.Core.Memory;

namespace Lumi.Tools.Tests.Memory;

/// <summary>
/// 記憶體版的 <see cref="IMemoryStore"/>，行為盡量貼近 SQLite 實作：指紋比對重複、上限算所有已儲存的記憶（含已過期）、id 不重用。
/// 可以指定下一次回傳的結果（例如模擬容量已滿），並記錄會變動的呼叫，讓測試檢查「被拒絕時 store 沒有被呼叫」。
/// </summary>
internal sealed class FakeMemoryStore(MemoryOptions options) : IMemoryStore
{
    public static readonly DateTimeOffset Stamp = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly List<MemoryItem> _items = [];
    private int _lastId;

    /// <summary>會變動的呼叫名稱（"add"、"update"、"delete"），給「沒有呼叫 store」的斷言用。</summary>
    public List<string> Calls { get; } = [];

    public NewMemory? LastAdd { get; private set; }

    public MemoryPatch? LastPatch { get; private set; }

    /// <summary>若設定，下一次 <see cref="AddAsync"/> 直接回傳這個結果（用完即清除）。</summary>
    public MemoryAddResult? NextAddResult { get; set; }

    /// <summary>若設定，下一次 <see cref="UpdateAsync"/> 直接回傳這個結果（用完即清除）。</summary>
    public MemoryUpdateResult? NextUpdateResult { get; set; }

    public IReadOnlyList<MemoryItem> Items => _items;

    /// <summary>直接放一則進去（不經過驗證，給測試準備資料；內容仍會正規化）。</summary>
    public MemoryItem Seed(string content, MemoryCategory category = MemoryCategory.Profile, DateOnly? expiresAt = null) =>
        Add(new MemoryItem(++_lastId, MemoryRules.NormalizeContent(content), category, MemorySource.Explicit, null, Stamp, Stamp, expiresAt));

    public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryItem>>(Sorted());

    public Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryItem>>(Sorted().Where(i => MemoryRules.IsActive(i, today)).ToList());

    public Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(_items.FirstOrDefault(i => i.Id == id));

    public MemoryItem? TryGet(int id) => _items.FirstOrDefault(i => i.Id == id);

    public Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default)
    {
        Calls.Add("add");
        LastAdd = memory;
        if (NextAddResult is { } forced)
        {
            NextAddResult = null;
            return Task.FromResult(forced);
        }

        var content = MemoryRules.NormalizeContent(memory.Content);
        var invalid = MemoryRules.ValidateContent(content, options, checkSecret: memory.Source != MemorySource.Manual);
        if (invalid is not null)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.Invalid, null, invalid));

        var existing = _items.FirstOrDefault(i => MemoryRules.Fingerprint(i.Content) == MemoryRules.Fingerprint(content));
        if (existing is not null)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.Duplicate, existing, null));

        if (_items.Count >= options.MaxItems)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.TooManyItems, null, null));
        if (TotalLength() + MemoryRules.Length(content) > options.MaxTotalLength)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.TooManyCharacters, null, null));

        var item = Add(new MemoryItem(++_lastId, content, memory.Category, memory.Source, memory.SourceConversationId, Stamp, Stamp, memory.ExpiresAt));
        return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.Added, item, null));
    }

    public Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default)
    {
        Calls.Add("update");
        LastPatch = patch;
        if (NextUpdateResult is { } forced)
        {
            NextUpdateResult = null;
            return Task.FromResult(forced);
        }

        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.NotFound, null, null));

        var old = _items[index];
        var content = patch.Content is null ? old.Content : MemoryRules.NormalizeContent(patch.Content);
        var invalid = MemoryRules.ValidateContent(content, options, checkSecret: !patch.SkipSecretCheck);
        if (invalid is not null)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.Invalid, null, invalid));

        var duplicate = _items.FirstOrDefault(i => i.Id != id && MemoryRules.Fingerprint(i.Content) == MemoryRules.Fingerprint(content));
        if (duplicate is not null)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.Duplicate, duplicate, null));

        var newTotal = TotalLength() - MemoryRules.Length(old.Content) + MemoryRules.Length(content);
        if (newTotal > options.MaxTotalLength)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.TooManyCharacters, null, null));

        var expiresAt = patch.ClearExpiresAt ? null : patch.ExpiresAt ?? old.ExpiresAt;
        var updated = old with
        {
            Content = content,
            Category = patch.Category ?? old.Category,
            ExpiresAt = expiresAt,
            UpdatedAt = Stamp,
        };
        _items[index] = updated;
        return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.Updated, updated, null));
    }

    public Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        Calls.Add("delete");
        return Task.FromResult(_items.RemoveAll(i => i.Id == id) > 0);
    }

    public Task<int> DeleteAllAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default) => throw new NotSupportedException();

    public CancellationToken ChangeToken => default;

    private MemoryItem Add(MemoryItem item)
    {
        _items.Add(item);
        return item;
    }

    private List<MemoryItem> Sorted() => _items.OrderBy(i => (int)i.Category).ThenBy(i => i.Id).ToList();

    private int TotalLength() => _items.Sum(i => MemoryRules.Length(i.Content));
}
