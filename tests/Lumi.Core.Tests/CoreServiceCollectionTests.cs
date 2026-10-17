using Lumi.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Core.Tests;

public class CoreServiceCollectionTests
{
    [Fact]
    public void AddLumiCore_BuildsServiceProvider()
    {
        var services = new ServiceCollection();

        services.AddLumiCore();

        using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.NotNull(provider);
    }
}
