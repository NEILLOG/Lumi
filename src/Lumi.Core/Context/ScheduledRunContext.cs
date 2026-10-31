using System.Globalization;

namespace Lumi.Core.Context;

/// <summary>排程紀錄當成上下文（引用卡）時共用的小工具（M24 4.7.3）：時間的顯示格式只在這裡定義一次。</summary>
public static class ScheduledRunContext
{
    /// <summary>執行時間的顯示格式（本地時間，固定格式，與地區設定無關）。</summary>
    public static string FormatTime(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
}
