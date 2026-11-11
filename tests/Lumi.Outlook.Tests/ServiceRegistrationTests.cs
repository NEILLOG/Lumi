using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Outlook.Tests;

public class ServiceRegistrationTests
{
    [Fact]
    public void AddLumiOutlook_returns_the_same_collection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddLumiOutlook());
    }
}
