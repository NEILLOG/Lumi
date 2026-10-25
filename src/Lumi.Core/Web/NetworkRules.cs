namespace Lumi.Core.Web;

/// <summary>
/// 網路設定的欄位驗證（M17 17A）。
/// 【暫時】這是 17D 為了在 17A 合併前能編譯而加的佔位實作，只回 null；17A 合併時以 17A 的檔案為準（整個覆蓋）。
/// </summary>
public static class NetworkRules
{
    /// <summary>回第一個錯誤的文字；沒有錯誤是 null。（暫時實作：一律通過）</summary>
    public static string? Validate(NetworkSettings settings) => null;
}
