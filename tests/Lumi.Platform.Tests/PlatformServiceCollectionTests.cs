using Lumi.Core.Secrets;
using Lumi.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Platform.Tests;

public class PlatformServiceCollectionTests
{
    [SkippableFact]
    public void AddLumiPlatform_registers_the_store_for_this_os()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "只支援 Windows 與 macOS");
        var services = new ServiceCollection();
        services.AddLumiPlatform(o => o.KeychainService = "io.github.neillog.lumi.tests");

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var store = provider.GetRequiredService<ISecretStore>();

        Assert.Same(store, provider.GetRequiredService<ISecretStore>()); // singleton
        var expected = OperatingSystem.IsMacOS() ? nameof(MacKeychainSecretStore) : nameof(WindowsDpapiSecretStore);
        Assert.Equal(expected, store.GetType().Name);
    }

    [Fact]
    public void Default_options_use_the_fixed_keychain_service_name()
    {
        Assert.Equal("io.github.neillog.lumi", new SecretStoreOptions().KeychainService);
        Assert.EndsWith(Path.Combine("Lumi", "secrets.json"), new SecretStoreOptions().ResolveDpapiFilePath());
    }
}
