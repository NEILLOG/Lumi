using Lumi.Core.Outlook;

namespace Lumi.Outlook;

/// <summary>把 COM 的錯誤碼轉成白話訊息。</summary>
internal static class OutlookErrors
{
    /// <summary>Outlook 沒有在限定時間內回應（多半是跳出了對話框）。</summary>
    public const string NotResponding = "Outlook 沒有回應，請看一下 Outlook 是不是跳出了視窗。";

    /// <summary>代號查不到（Lumi 重開過，或已超出記憶的數量）。</summary>
    public const string HandleExpired = "這個代號已經失效，請重新搜尋。";

    private const int PermissionLevelMismatch = unchecked((int)0x80080005);
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int AccessDenied = unchecked((int)0x80004004);

    public static OutlookException FromHResult(int hresult, string message) => hresult switch
    {
        PermissionLevelMismatch => new OutlookException("Lumi 和 Outlook 的權限等級不同（其中一個用系統管理員執行），請用一般方式重新開啟兩者。"),
        RpcServerUnavailable or RpcDisconnected => new OutlookException("Outlook 已經關閉，請開啟後再試一次。"),
        AccessDenied => new OutlookException("Outlook 拒絕了這次存取。"),
        _ => new OutlookException($"Outlook 回報錯誤：{message}"),
    };
}
