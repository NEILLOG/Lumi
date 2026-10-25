using Lumi.Core.Web;

namespace Lumi.Core.Tests.Web;

public class NetworkSettingsTests
{
    [Fact]
    public void Equality_compares_list_contents()
    {
        var a = NetworkSettings.Default with { BypassList = ["a.example", "b.example"], IntegratedAuthSites = ["*.corp"] };
        var b = NetworkSettings.Default with { BypassList = ["a.example", "b.example"], IntegratedAuthSites = ["*.corp"] };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Different_list_contents_are_not_equal()
    {
        var a = NetworkSettings.Default with { BypassList = ["a.example"] };
        var b = NetworkSettings.Default with { BypassList = ["b.example"] };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Normalize_cleans_lists_and_clamps_timeout()
    {
        var s = (NetworkSettings.Default with
        {
            BypassList = [" B.example ", "", "a.example", "A.EXAMPLE"],
            TimeoutSeconds = 9999,
        }).Normalize();

        Assert.Equal(["a.example", "b.example"], s.BypassList);
        Assert.Equal(NetworkSettings.MaxTimeoutSeconds, s.TimeoutSeconds);
    }
}
