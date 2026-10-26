using System.Runtime.Versioning;
using Lumi.Platform.Interop.Mac;
using static Lumi.Platform.Interop.Mac.ObjC;

namespace Lumi.Platform.Shell;

/// <summary>
/// macOS Dock 圖示（M20 20E）：執行期用 AppKit 設定 <c>NSApplication</c> 的應用程式圖示，
/// 開發時（沒有 .app 套件、Info.plist 不生效）也能顯示光球。
/// </summary>
/// <remarks>
/// 必須在 UI 執行緒呼叫。任何失敗都回傳 false，不丟例外。
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacDockIcon
{
    /// <summary>用 PNG 位元組設定 Dock 圖示；成功回 true。</summary>
    public static bool TryApply(byte[] png)
    {
        try
        {
            if (png is null || png.Length == 0)
                return false;

            var dataClass = objc_getClass("NSData");
            var imageClass = objc_getClass("NSImage");
            var applicationClass = objc_getClass("NSApplication");
            if (dataClass == 0 || imageClass == 0 || applicationClass == 0)
                return false;

            // NSData dataWithBytes:length:（會複製一份，所以只需在呼叫期間釘住陣列）
            nint data;
            unsafe
            {
                fixed (byte* bytes = png)
                {
                    data = SendPtrBytesLength(dataClass, Sel("dataWithBytes:length:"), (nint)bytes, (nuint)png.Length);
                }
            }
            if (data == 0)
                return false;

            // [[NSImage alloc] initWithData:data]：alloc 出來的 +1，設定完要 release
            var allocated = SendPtr(imageClass, Sel("alloc"));
            if (allocated == 0)
                return false;
            var image = SendPtrArg(allocated, Sel("initWithData:"), data);
            if (image == 0)
                return false;

            var application = SendPtr(applicationClass, Sel("sharedApplication"));
            if (application == 0)
            {
                SendPtr(image, Sel("release"));
                return false;
            }

            // setApplicationIconImage: 會自己 retain，所以這裡的 +1 可以放掉
            SendPtrArg(application, Sel("setApplicationIconImage:"), image);
            SendPtr(image, Sel("release"));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
