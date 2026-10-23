namespace Lumi.Core.Reminders;

/// <summary>
/// 提醒與排程的限制數字（沿用舊版）。
/// </summary>
public sealed class ReminderOptions
{
    /// <summary>未完成（待觸發）的項目上限，含停用的。</summary>
    public int MaxPendingItems { get; set; } = 200;

    /// <summary>一次性提醒過了這段時間還沒響，就標為錯過（不響，只彙總提示）。</summary>
    public TimeSpan MissedGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>已結束（完成、取消、錯過）的項目保留多久，之後清除。</summary>
    public TimeSpan FinishedRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>背景迴圈最長的等待時間；讓系統時鐘被調整、睡眠喚醒後最多晚這段時間發現。</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(60);

    public int MaxTitleChars { get; set; } = 100;

    public int MaxMessageChars { get; set; } = 500;

    public int MinTimerSeconds { get; set; } = 10;

    public int MaxTimerSeconds { get; set; } = 86_400;
}
