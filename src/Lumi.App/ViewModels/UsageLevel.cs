namespace Lumi.App.ViewModels;

/// <summary>本對話上下文用量的等級：決定用量圖示的顏色（ui-usage-and-topbar.md 2.1）。</summary>
public enum UsageLevel
{
    /// <summary>還沒有任何 usage。</summary>
    Empty,

    /// <summary>小於 60%。</summary>
    Normal,

    /// <summary>60% 以上、未滿 85%。</summary>
    Warning,

    /// <summary>85% 以上。</summary>
    Danger,
}

public static class UsageLevelRules
{
    public const double WarningThreshold = 0.60;
    public const double DangerThreshold = 0.85;

    public static UsageLevel From(bool hasData, double ratio)
    {
        if (!hasData)
            return UsageLevel.Empty;
        if (ratio >= DangerThreshold)
            return UsageLevel.Danger;
        return ratio >= WarningThreshold ? UsageLevel.Warning : UsageLevel.Normal;
    }
}
