using System.Globalization;

namespace Lumi.Core.Memory;

/// <summary>
/// 記憶的寫入規則（純函式）：新增與修改時的檢查順序與訊息。
/// <see cref="InMemoryMemoryStore"/> 與 SQLite 版都只呼叫這裡，所以兩者的判斷不會分岔；
/// store 自己只負責在同一個交易（或同一把鎖）裡讀出既有資料、呼叫這裡、再寫入。
/// </summary>
public static class MemoryWriteRules
{
    /// <summary>
    /// 新增的檢查：Invalid → Duplicate → TooManyItems → TooManyCharacters。全部通過回 null。
    /// <paramref name="normalized"/> 必須已經是 <see cref="MemoryRules.NormalizeContent"/> 的結果。
    /// </summary>
    /// <param name="category">不是已定義的分類時回 Invalid（寫進去之後讀取會被略過）。</param>
    public static MemoryAddResult? CheckAdd(string normalized, MemoryCategory category, MemorySource source, IReadOnlyList<MemoryItem> existing, MemoryOptions options)
    {
        if (!Enum.IsDefined(category))
            return new MemoryAddResult(MemoryAddOutcome.Invalid, null, "分類不正確。");
        var invalid = MemoryRules.ValidateContent(normalized, options, checkSecret: source != MemorySource.Manual);
        if (invalid is not null)
            return new MemoryAddResult(MemoryAddOutcome.Invalid, null, invalid);

        var fingerprint = MemoryRules.Fingerprint(normalized);
        var same = existing.FirstOrDefault(item => MemoryRules.Fingerprint(item.Content) == fingerprint);
        if (same is not null)
            return new MemoryAddResult(MemoryAddOutcome.Duplicate, same, DuplicateMessage(same.Id));

        if (existing.Count >= options.MaxItems)
            return new MemoryAddResult(MemoryAddOutcome.TooManyItems, null, $"長期記憶已滿（{Number(existing.Count)} 則，上限 {Number(options.MaxItems)} 則）。");

        var total = TotalLength(existing);
        var length = MemoryRules.Length(normalized);
        if (total + length > options.MaxTotalLength)
            return new MemoryAddResult(
                MemoryAddOutcome.TooManyCharacters,
                null,
                $"長期記憶的總字數會超過上限（目前 {Number(total)} 字，這則 {Number(length)} 字，上限 {Number(options.MaxTotalLength)} 字）。");

        return null;
    }

    /// <summary>
    /// 修改的檢查：NotFound → Invalid（沒有欄位）→ Invalid（內容）→ Duplicate（與另一則相同）→ TooManyCharacters（內容變長超過總字數）。
    /// 全部通過回 null。<paramref name="target"/> 為 null 表示找不到；<paramref name="normalized"/> 為 null 表示沒有要改內容。
    /// 總字數只在內容變長時檢查：縮短永遠允許，即使目前已經超過上限（例如上限被調低過）。
    /// </summary>
    public static MemoryUpdateResult? CheckUpdate(
        int id,
        MemoryItem? target,
        MemoryPatch patch,
        string? normalized,
        IReadOnlyList<MemoryItem> existing,
        MemoryOptions options)
    {
        if (target is null)
            return new MemoryUpdateResult(MemoryUpdateOutcome.NotFound, null, $"找不到記憶 {MemoryRules.FormatId(id)}。");
        if (patch.IsEmpty)
            return new MemoryUpdateResult(MemoryUpdateOutcome.Invalid, null, "沒有要修改的欄位。");
        if (normalized is null)
            return null;

        var invalid = MemoryRules.ValidateContent(normalized, options, checkSecret: !patch.SkipSecretCheck);
        if (invalid is not null)
            return new MemoryUpdateResult(MemoryUpdateOutcome.Invalid, null, invalid);

        var fingerprint = MemoryRules.Fingerprint(normalized);
        var other = existing.FirstOrDefault(item => item.Id != target.Id && MemoryRules.Fingerprint(item.Content) == fingerprint);
        if (other is not null)
            return new MemoryUpdateResult(MemoryUpdateOutcome.Duplicate, other, DuplicateMessage(other.Id));

        var oldLength = MemoryRules.Length(target.Content);
        var newLength = MemoryRules.Length(normalized);
        if (newLength > oldLength)
        {
            var newTotal = TotalLength(existing) - oldLength + newLength;
            if (newTotal > options.MaxTotalLength)
                return new MemoryUpdateResult(
                    MemoryUpdateOutcome.TooManyCharacters,
                    null,
                    $"修改後長期記憶的總字數會超過上限（修改後約 {Number(newTotal)} 字，上限 {Number(options.MaxTotalLength)} 字）。");
        }

        return null;
    }

    /// <summary>套用修改：沒給的欄位不變；<see cref="MemoryPatch.ClearExpiresAt"/> 優先於 <see cref="MemoryPatch.ExpiresAt"/>。Source、CreatedAt、SourceConversationId 不變。</summary>
    public static MemoryItem Apply(MemoryItem target, MemoryPatch patch, string? normalized, DateTimeOffset now) => target with
    {
        Content = normalized ?? target.Content,
        Category = patch.Category ?? target.Category,
        ExpiresAt = patch.ClearExpiresAt ? null : patch.ExpiresAt ?? target.ExpiresAt,
        UpdatedAt = now,
    };

    /// <summary>所有記憶的字數加總（字形叢集）。</summary>
    public static int TotalLength(IEnumerable<MemoryItem> items) => items.Sum(item => MemoryRules.Length(item.Content));

    private static string DuplicateMessage(int id) => $"已經有相同內容的記憶（{MemoryRules.FormatId(id)}）。";

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
