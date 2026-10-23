using Lumi.Core.Memory;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 記憶的記憶體替身（設定頁測試用）。規則走 <see cref="MemoryRules"/>，上限與去重的行為和正式實作相同。
/// <see cref="Notify"/> 手動觸發 <see cref="ChangeToken"/>（每次換新權杖，做法同正式實作）；
/// <see cref="NextAddResult"/>、<see cref="NextUpdateResult"/> 可指定下一次的結果（不改資料），用來測各種失敗訊息。
/// </summary>
internal sealed class FakeMemoryStore : IMemoryStore
{
    private readonly List<MemoryItem> _items = [];
    private readonly MemoryOptions _options;
    private int _nextId = 1;
    private CancellationTokenSource _changes = new();

    public FakeMemoryStore(MemoryOptions? options = null)
    {
        _options = options ?? new MemoryOptions();
    }

    /// <summary>目前的時間（建立或修改時寫進紀錄）。</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<MemoryItem> Items => _items;

    public List<NewMemory> AddCalls { get; } = [];

    public List<(int Id, MemoryPatch Patch)> UpdateCalls { get; } = [];

    public List<int> DeleteCalls { get; } = [];

    public int DeleteAllCalls { get; private set; }

    /// <summary>下一次 <see cref="AddAsync"/> 直接回傳的結果（用過就清掉，資料不變）。</summary>
    public MemoryAddResult? NextAddResult { get; set; }

    /// <summary>下一次 <see cref="UpdateAsync"/> 直接回傳的結果（用過就清掉，資料不變）。</summary>
    public MemoryUpdateResult? NextUpdateResult { get; set; }

    /// <summary>直接加入一則記憶（不經過規則，給測試準備資料）並通知變動。</summary>
    public MemoryItem Seed(string content, MemoryCategory category, MemorySource source = MemorySource.Manual, DateOnly? expires = null)
    {
        var item = new MemoryItem(_nextId++, MemoryRules.NormalizeContent(content), category, source, null, Now, Now, expires);
        _items.Add(item);
        Notify();
        return item;
    }

    /// <summary>手動觸發變動（模擬模型在對話中存了記憶）。</summary>
    public void Notify()
    {
        var previous = _changes;
        _changes = new CancellationTokenSource();
        previous.Cancel();
    }

    public Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryItem>>(_items.OrderBy(i => (int)i.Category).ThenBy(i => i.Id).ToList());

    public Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MemoryItem>>(_items.Where(i => MemoryRules.IsActive(i, today))
            .OrderBy(i => (int)i.Category).ThenBy(i => i.Id).ToList());

    public Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default) => Task.FromResult(TryGet(id));

    public MemoryItem? TryGet(int id) => _items.FirstOrDefault(i => i.Id == id);

    public Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default)
    {
        AddCalls.Add(memory);
        if (NextAddResult is { } forced)
        {
            NextAddResult = null;
            return Task.FromResult(forced);
        }

        var normalized = MemoryRules.NormalizeContent(memory.Content);
        var problem = MemoryRules.ValidateContent(normalized, _options, checkSecret: memory.Source != MemorySource.Manual);
        if (problem is not null)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.Invalid, null, problem));

        var fingerprint = MemoryRules.Fingerprint(normalized);
        if (_items.FirstOrDefault(i => MemoryRules.Fingerprint(i.Content) == fingerprint) is { } same)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.Duplicate, same, null));
        if (_items.Count >= _options.MaxItems)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.TooManyItems, null, $"已經有 {_options.MaxItems} 則記憶，請先刪掉不需要的。"));
        if (TotalLength() + MemoryRules.Length(normalized) > _options.MaxTotalLength)
            return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.TooManyCharacters, null, "記憶的總字數已經超過上限，請先刪掉或縮短一些。"));

        var item = new MemoryItem(_nextId++, normalized, memory.Category, memory.Source, memory.SourceConversationId, Now, Now, memory.ExpiresAt);
        _items.Add(item);
        Notify();
        return Task.FromResult(new MemoryAddResult(MemoryAddOutcome.Added, item, null));
    }

    public Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default)
    {
        UpdateCalls.Add((id, patch));
        if (NextUpdateResult is { } forced)
        {
            NextUpdateResult = null;
            return Task.FromResult(forced);
        }

        var index = _items.FindIndex(i => i.Id == id);
        if (index < 0)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.NotFound, null, "找不到這則記憶。"));
        var current = _items[index];

        var content = patch.Content is null ? current.Content : MemoryRules.NormalizeContent(patch.Content);
        if (patch.Content is not null)
        {
            var problem = MemoryRules.ValidateContent(content, _options, checkSecret: !patch.SkipSecretCheck);
            if (problem is not null)
                return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.Invalid, null, problem));
        }

        var fingerprint = MemoryRules.Fingerprint(content);
        if (_items.FirstOrDefault(i => i.Id != id && MemoryRules.Fingerprint(i.Content) == fingerprint) is { } same)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.Duplicate, same, null));
        if (TotalLength() - MemoryRules.Length(current.Content) + MemoryRules.Length(content) > _options.MaxTotalLength)
            return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.TooManyCharacters, null, "記憶的總字數已經超過上限，請先刪掉或縮短一些。"));

        var expires = patch.ClearExpiresAt ? null : patch.ExpiresAt ?? current.ExpiresAt;
        var updated = current with
        {
            Content = content,
            Category = patch.Category ?? current.Category,
            ExpiresAt = expires,
            UpdatedAt = Now,
        };
        _items[index] = updated;
        Notify();
        return Task.FromResult(new MemoryUpdateResult(MemoryUpdateOutcome.Updated, updated, null));
    }

    public Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        DeleteCalls.Add(id);
        var removed = _items.RemoveAll(i => i.Id == id) > 0;
        if (removed)
            Notify();
        return Task.FromResult(removed);
    }

    public Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        DeleteAllCalls++;
        var count = _items.Count;
        _items.Clear();
        Notify();
        return Task.FromResult(count);
    }

    public Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default)
    {
        var cutoff = today.AddDays(-graceDays);
        var count = _items.RemoveAll(i => i.ExpiresAt is { } expires && expires < cutoff);
        if (count > 0)
            Notify();
        return Task.FromResult(count);
    }

    public CancellationToken ChangeToken => _changes.Token;

    private int TotalLength() => _items.Sum(i => MemoryRules.Length(i.Content));
}
