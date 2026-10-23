using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

/// <summary>歷史抽屜的數字集中在這裡（plan/09「其他我直接定的事」）。</summary>
public sealed record HistoryOptions
{
    public static HistoryOptions Default { get; } = new();

    /// <summary>搜尋框輸入後，停止輸入這麼久才查詢（節流）。</summary>
    public int SearchDebounceMilliseconds { get; init; } = 250;

    /// <summary>清單一頁的對話數；「載入更多」每次載入這麼多。</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>搜尋結果上限；達到上限時顯示「只顯示前 N 筆」。</summary>
    public int SearchLimit { get; init; } = 100;

    /// <summary>改名後的標題上限（以 Unicode 字元計）。自動標題（30 字）不受影響。</summary>
    public int TitleMaxLength { get; init; } = 60;

    /// <summary>
    /// 命中片段前後各取幾個字。實際擷取在 Core 的 <see cref="SearchSnippet"/>（存儲層產生片段），這裡只是同一個數字的參照。
    /// </summary>
    public int SnippetContext => SearchSnippet.Context;
}
