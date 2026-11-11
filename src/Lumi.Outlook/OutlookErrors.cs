using Lumi.Core.Outlook;

namespace Lumi.Outlook;

/// <summary>把 COM 的錯誤碼轉成白話訊息。</summary>
internal static class OutlookErrors
{
    public static OutlookException FromHResult(int hresult, string message) => throw new NotImplementedException(); // TODO(30A)
}
