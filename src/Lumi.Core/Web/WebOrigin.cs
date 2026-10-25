namespace Lumi.Core.Web;

/// <summary>
/// 「網站」的比對鍵（M17 D4）：scheme＋主機＋port，全部小寫，預設 port 也寫出來（<c>https://example.com:443</c>）。
/// </summary>
public static class WebOrigin
{
    public static string Of(Uri url) =>
        $"{url.Scheme}://{url.IdnHost}:{url.Port}".ToLowerInvariant();
}
