namespace Lumi.Core.Memory;

/// <summary>記憶的分類。數值同舊版 <c>memory.dat</c>（M13 匯入不必翻譯）。</summary>
public enum MemoryCategory { Profile = 0, Preference = 1, Work = 2, Project = 3 }

/// <summary>
/// 記憶的來源。數值同舊版。<see cref="Explicit"/>＝使用者的訊息含觸發詞（由程式判定，不信模型自己宣告）；
/// <see cref="Suggested"/>＝模型提議、使用者在確認卡片同意；<see cref="Manual"/>＝使用者在設定頁手動新增。
/// </summary>
public enum MemorySource { Explicit = 0, Suggested = 1, Manual = 2 }

/// <param name="Id">資料庫自動產生、刪除後不重用；顯示與工具參數用 <c>m&lt;數字&gt;</c>（<see cref="MemoryRules.FormatId"/>）。</param>
/// <param name="SourceConversationId">來源對話，僅供參考，沒有外鍵：刪除對話不會刪除記憶。</param>
/// <param name="ExpiresAt">到期日（含當天）；null＝永久。</param>
public sealed record MemoryItem(
    int Id,
    string Content,
    MemoryCategory Category,
    MemorySource Source,
    string? SourceConversationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateOnly? ExpiresAt);

/// <summary>要新增的記憶。<paramref name="Content"/> 可以是原文，store 會用 <see cref="MemoryRules.NormalizeContent"/> 整理。</summary>
/// <remarks><see cref="MemorySource.Manual"/>（設定頁手動新增）略過機密樣式檢查，其他檢查照舊。</remarks>
public sealed record NewMemory(
    string Content,
    MemoryCategory Category,
    MemorySource Source,
    string? SourceConversationId = null,
    DateOnly? ExpiresAt = null);

/// <summary>
/// 修改記憶：沒給的欄位不變；<see cref="ClearExpiresAt"/> 為 true 時清除到期日（優先於 <see cref="ExpiresAt"/>）。
/// <see cref="SkipSecretCheck"/>：使用者在設定頁親手編輯時為 true（略過機密樣式檢查，其他檢查照舊）；模型經由工具修改時為 false。
/// </summary>
public sealed record MemoryPatch(
    string? Content = null,
    MemoryCategory? Category = null,
    DateOnly? ExpiresAt = null,
    bool ClearExpiresAt = false,
    bool SkipSecretCheck = false)
{
    public bool IsEmpty => Content is null && Category is null && ExpiresAt is null && !ClearExpiresAt;
}

public enum MemoryAddOutcome
{
    Added,
    /// <summary>已經有相同內容的記憶；<see cref="MemoryAddResult.Item"/> 是那一則。</summary>
    Duplicate,
    TooManyItems,
    TooManyCharacters,
    /// <summary>內容空白、太長或看起來像機密；<see cref="MemoryAddResult.Message"/> 是可以直接給模型或使用者看的原因。</summary>
    Invalid,
}

public sealed record MemoryAddResult(MemoryAddOutcome Outcome, MemoryItem? Item, string? Message)
{
    public bool Succeeded => Outcome == MemoryAddOutcome.Added;
}

public enum MemoryUpdateOutcome { Updated, NotFound, Duplicate, TooManyCharacters, Invalid }

public sealed record MemoryUpdateResult(MemoryUpdateOutcome Outcome, MemoryItem? Item, string? Message)
{
    public bool Succeeded => Outcome == MemoryUpdateOutcome.Updated;
}

/// <summary>
/// 長期記憶的存取。實作：Infrastructure（SQLite）與測試用的記憶體替身，兩者行為一致。
/// 上限（則數、單則字數、總字數）算<b>所有已儲存的</b>記憶（含已過期、尚未清除的）；超過時不自動淘汰，回結果由呼叫端處理。
/// 規則一律走 <see cref="MemoryRules"/>，store 只負責存取與在同一個交易內檢查上限。
/// </summary>
public interface IMemoryStore
{
    /// <summary>全部（含已過期），依分類再依 id 排序。設定頁用。</summary>
    Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default);

    /// <summary>有效的（<see cref="MemoryRules.IsActive"/>），依分類再依 id 排序。組 prompt 用。</summary>
    Task<IReadOnlyList<MemoryItem>> ListActiveAsync(DateOnly today, CancellationToken ct = default);

    Task<MemoryItem?> GetAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// 同步讀一則（含已過期）；找不到回 null。只給 <c>ITool.Preview</c> 用（它是同步的，確認卡片要顯示「忘掉哪一則」）：
    /// 本機 SQLite 的單筆讀取很快。其他地方一律用 <see cref="GetAsync"/>。
    /// </summary>
    MemoryItem? TryGet(int id);

    Task<MemoryAddResult> AddAsync(NewMemory memory, CancellationToken ct = default);

    Task<MemoryUpdateResult> UpdateAsync(int id, MemoryPatch patch, CancellationToken ct = default);

    /// <summary>找不到回 false。</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>刪除全部，回傳刪掉幾則。</summary>
    Task<int> DeleteAllAsync(CancellationToken ct = default);

    /// <summary>刪除到期超過 <paramref name="graceDays"/> 天的記憶，回傳刪掉幾則。</summary>
    Task<int> PurgeExpiredAsync(DateOnly today, int graceDays, CancellationToken ct = default);

    /// <summary>
    /// 每次內容變動（新增、修改、刪除、清除）就取消目前的權杖並換一個新的；訂閱者先取得權杖、再讀資料，就不會漏掉變動。
    /// 做法同 <c>ReminderService.ChangeToken</c>。
    /// </summary>
    CancellationToken ChangeToken { get; }
}

/// <summary>第 5 層（記憶）需要的輸入；<see cref="PromptContext.Memory"/> 為 null＝記憶已關閉。<paramref name="Items"/> 必須是有效的記憶。</summary>
public sealed record MemoryPromptContext(IReadOnlyList<MemoryItem> Items);

public sealed class MemoryOptions
{
    public int MaxItems { get; set; } = 100;

    /// <summary>單則上限，以字形叢集計（中文、emoji 各算一字）。</summary>
    public int MaxContentLength { get; set; } = 200;

    /// <summary>所有記憶的總字數上限（字形叢集）。</summary>
    public int MaxTotalLength { get; set; } = 2000;

    /// <summary>到期超過幾天後，啟動時物理刪除。</summary>
    public int ExpiredGraceDays { get; set; } = 30;

    /// <summary>判斷「使用者明確要求記住／忘記」的關鍵字（不分大小寫、子字串比對）。</summary>
    public List<string> TriggerWords { get; set; } = ["記住", "記得", "別忘了", "以後", "忘掉", "remember", "forget"];
}
