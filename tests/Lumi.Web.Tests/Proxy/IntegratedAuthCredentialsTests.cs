using System.Net;
using Lumi.Core.Web;
using Lumi.Web.Proxy;
using Xunit;

namespace Lumi.Web.Tests.Proxy;

public class IntegratedAuthCredentialsTests
{
    private static IntegratedAuthCredentials Create(params string[] sites) =>
        new(new FakeCatalog(new NetworkSettings { IntegratedAuthSites = sites }));

    [Theory]
    [InlineData("Negotiate")]
    [InlineData("NTLM")]
    [InlineData("negotiate")]
    [InlineData("Kerberos")]
    public void Listed_site_with_windows_auth_gets_credentials(string authType)
    {
        var creds = Create("*.corp.example", "intranet");
        Assert.Same(CredentialCache.DefaultNetworkCredentials, creds.GetCredential(new Uri("https://wiki.corp.example/x"), authType));
        Assert.Same(CredentialCache.DefaultNetworkCredentials, creds.GetCredential(new Uri("http://intranet/"), authType));
    }

    [Fact]
    public void Unlisted_site_gets_nothing()
    {
        var creds = Create("*.corp.example");
        Assert.Null(creds.GetCredential(new Uri("https://evil.example/"), "Negotiate"));
        Assert.Null(creds.GetCredential(new Uri("https://corp.example.evil.com/"), "NTLM"));
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Digest")]
    public void Other_auth_types_get_nothing(string authType)
    {
        Assert.Null(Create("*.corp.example").GetCredential(new Uri("https://wiki.corp.example/"), authType));
    }

    [Fact]
    public void Empty_list_gets_nothing()
    {
        Assert.Null(Create().GetCredential(new Uri("https://wiki.corp.example/"), "Negotiate"));
    }

    [Fact]
    public void Follows_settings_changes()
    {
        var catalog = new FakeCatalog(new NetworkSettings());
        var creds = new IntegratedAuthCredentials(catalog);
        Assert.Null(creds.GetCredential(new Uri("https://a.example/"), "Negotiate"));
        catalog.SetNetwork(new NetworkSettings { IntegratedAuthSites = ["a.example"] });
        Assert.NotNull(creds.GetCredential(new Uri("https://a.example/"), "Negotiate"));
    }
}
