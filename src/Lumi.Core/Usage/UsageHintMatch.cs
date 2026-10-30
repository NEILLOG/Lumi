namespace Lumi.Core.Usage;

/// <summary>
/// 一則符合的提示（plan/23-usage-cost.md 第 7 節）。
/// <paramref name="Rule"/> 是 <see cref="UsageHintIds"/> 的值。
/// <paramref name="Args"/> 是提示文字的格式參數，已經格式化好，App 只負責填進字串表（第 2 條沒有參數；其餘各條見 <see cref="UsageHintRules"/>）。
/// <paramref name="Key"/> 是同一則對話內去重用的鍵：第 4 條放檔名，其餘為 null。
/// </summary>
public sealed record UsageHintMatch(string Rule, IReadOnlyList<string> Args, string? Key = null);
