using Lumi.Core.Secrets;
using Lumi.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>註冊平台相關實作（目前只有 <see cref="ISecretStore"/>），依作業系統選擇。</summary>
    public static IServiceCollection AddLumiPlatform(
        this IServiceCollection services, Action<SecretStoreOptions>? configureSecrets = null)
    {
        var options = services.AddOptions<SecretStoreOptions>();
        if (configureSecrets is not null)
            options.Configure(configureSecrets);

        if (OperatingSystem.IsWindows())
            services.AddSingleton<ISecretStore, WindowsDpapiSecretStore>();
        else if (OperatingSystem.IsMacOS())
            services.AddSingleton<ISecretStore, MacKeychainSecretStore>();
        else
            throw new PlatformNotSupportedException("Lumi 只支援 Windows 與 macOS");

        return services;
    }
}
