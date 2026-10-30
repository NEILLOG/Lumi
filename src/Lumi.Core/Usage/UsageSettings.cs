namespace Lumi.Core.Usage;

/// <param name="MonthlyBudgetUsd">每月預算（美元）；null＝不設。UI 輸入新台幣，存的時候除以 <see cref="Money.TwdPerUsd"/>。</param>
/// <param name="MutedHints">被「不再提醒這類」的提示 id（<see cref="UsageHintIds"/>）。</param>
public sealed record UsageSettings(decimal? MonthlyBudgetUsd = null, IReadOnlyList<string>? MutedHints = null)
{
    public static readonly UsageSettings Default = new();

    public IReadOnlyList<string> Muted => MutedHints ?? [];

    /// <summary>預算 ≤ 0 當作不設；MutedHints 去重、只留認得的 id。</summary>
    public UsageSettings Normalize() => this with
    {
        MonthlyBudgetUsd = MonthlyBudgetUsd is > 0 ? MonthlyBudgetUsd : null,
        MutedHints = Muted.Where(UsageHintIds.All.Contains).Distinct(StringComparer.Ordinal).ToList(),
    };
}

public static class Money
{
    /// <summary>固定匯率（決定 3）；之後開放設定時改這裡。</summary>
    public const decimal TwdPerUsd = 32.0m;
}
