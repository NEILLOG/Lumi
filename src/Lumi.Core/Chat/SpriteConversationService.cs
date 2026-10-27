namespace Lumi.Core.Chat;

/// <summary>
/// 小精靈專屬對話（M22）：小精靈固定使用<b>一則自己的對話</b>，不跟著工作台切換。對話 id 存在 <c>app_meta</c>
/// （key <see cref="MetaKey"/>）；第一次需要時建立，id 對不到（對話被刪、資料庫被手動改）時重新建立一則。
/// 同步的 <see cref="CachedId"/> 給畫面判斷「這是不是小精靈的對話」用（歷史清單釘選、「⋯」選單換成「清除內容」）。
/// </summary>
public sealed class SpriteConversationService(IConversationStore store)
{
    /// <summary><c>app_meta</c> 裡記小精靈對話 id 的 key。</summary>
    public const string MetaKey = "sprite_conversation_id";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile string? _cachedId;

    /// <summary>最近一次確認過的小精靈對話 id；還沒建立（或還沒查過）是 null。</summary>
    public string? CachedId => _cachedId;

    public bool IsSpriteConversation(string? conversationId) => conversationId is not null && conversationId == _cachedId;

    /// <summary>小精靈對話；還沒建立，或記的 id 對不到對話時回 null（不會建立）。</summary>
    public async Task<Conversation?> GetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FindAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>小精靈對話的 id（對得到對話才有）；沒有回 null。</summary>
    public async Task<string?> GetIdAsync(CancellationToken ct = default) => (await GetAsync(ct).ConfigureAwait(false))?.Id;

    /// <summary>取得小精靈對話，沒有就用 <paramref name="title"/> 建立並記下 id。</summary>
    public async Task<Conversation> GetOrCreateAsync(string title, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await FindAsync(ct).ConfigureAwait(false) is { } existing)
                return existing;

            var created = await store.CreateAsync(title, ct).ConfigureAwait(false);
            await store.SetMetaAsync(MetaKey, created.Id, ct).ConfigureAwait(false);
            _cachedId = created.Id;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Conversation?> FindAsync(CancellationToken ct)
    {
        var id = await store.GetMetaAsync(MetaKey, ct).ConfigureAwait(false);
        var conversation = string.IsNullOrEmpty(id) ? null : await store.GetConversationAsync(id, ct).ConfigureAwait(false);
        _cachedId = conversation?.Id;
        return conversation;
    }
}
