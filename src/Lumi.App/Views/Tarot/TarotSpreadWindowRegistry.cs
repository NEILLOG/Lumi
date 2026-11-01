using Avalonia.Controls;
using Lumi.Tarot;

namespace Lumi.App.Views.Tarot;

/// <summary>
/// 凱爾特十字牌陣視窗的登錄表（plan/25 25F）。同一個附件（以 CallId 為 key）只會開一個視窗，重複開啟只把已開啟的視窗帶到前面；
/// 視窗關閉時移除。<see cref="Default"/> 是應用程式共用的靜態登錄表，測試可以另外 new 一個。
/// </summary>
public sealed class TarotSpreadWindowRegistry
{
    /// <summary>應用程式共用的登錄表（只在 UI 執行緒使用）。</summary>
    public static TarotSpreadWindowRegistry Default { get; } = new();

    private readonly Dictionary<string, TarotSpreadWindow> _windows = new(StringComparer.Ordinal);

    /// <summary>目前開啟中的視窗數。</summary>
    public int Count => _windows.Count;

    /// <summary>
    /// 開啟 <paramref name="key"/> 對應的牌陣視窗。已開啟就還原（最小化時）並帶到前面，回傳 <c>Created = false</c>；否則建立、顯示並登錄。
    /// </summary>
    public (TarotSpreadWindow Window, bool Created) Open(string key, TarotSpreadModel spread, string? conversationTitle)
    {
        if (_windows.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return (existing, false);
        }

        var window = new TarotSpreadWindow(spread, conversationTitle);
        _windows[key] = window;
        window.Closed += (_, _) =>
        {
            // 只移除仍是這個視窗的登錄（同 key 不會同時存在兩個，但保守起見比對參考）
            if (_windows.TryGetValue(key, out var current) && ReferenceEquals(current, window))
                _windows.Remove(key);
        };
        window.Show();
        return (window, true);
    }
}
