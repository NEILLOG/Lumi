using System.Net;
using Lumi.Core.Models;

namespace Lumi.Web.Proxy;

/// <summary>
/// 網路工具連到使用者列出的網站時，伺服器要求 Negotiate／NTLM 就送出目前 Windows 登入者的身分（M17 D8）。
/// 其他網站與其他驗證方式一律不送。
/// </summary>
public sealed class IntegratedAuthCredentials(IModelCatalog catalog) : ICredentials
{
    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        if (!authType.Equals("Negotiate", StringComparison.OrdinalIgnoreCase)
            && !authType.Equals("NTLM", StringComparison.OrdinalIgnoreCase)
            && !authType.Equals("Kerberos", StringComparison.OrdinalIgnoreCase))
            return null;

        return NetworkRulesStub.HostMatches(uri.Host, catalog.Current.Network.IntegratedAuthSites)
            ? CredentialCache.DefaultNetworkCredentials
            : null;
    }
}
