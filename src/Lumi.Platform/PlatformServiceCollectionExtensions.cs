using Lumi.Core.Platform;
using Lumi.Core.Secrets;
using Lumi.Platform.Overlay;
using Lumi.Platform.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lumi.Platform;

public static class PlatformServiceCollectionExtensions
{
    /// <summary>註冊平台相關實作（<see cref="ISecretStore"/>、<see cref="IOverlayPlatform"/>），依作業系統選擇。</summary>
    public static IServiceCollection AddLumiPlatform(
        this IServiceCollection services, Action<SecretStoreOptions>? configureSecrets = null, Action<OverlayOptions>? configureOverlay = null)
    {
        var options = services.AddOptions<SecretStoreOptions>();
        if (configureSecrets is not null)
            options.Configure(configureSecrets);

        var overlay = services.AddOptions<OverlayOptions>();
        if (configureOverlay is not null)
            overlay.Configure(configureOverlay);

        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<ISecretStore, WindowsDpapiSecretStore>();
            // 每個視窗各用一個實例（Transient）
            services.AddTransient<IOverlayPlatform>(sp => OperatingSystem.IsWindows()
                ? new WindowsOverlay(sp.GetRequiredService<IOptions<OverlayOptions>>().Value.Layered)
                : throw new PlatformNotSupportedException());
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<ISecretStore, MacKeychainSecretStore>();
            services.AddTransient<IOverlayPlatform, MacOverlay>();
        }
        else
        {
            throw new PlatformNotSupportedException("Lumi 只支援 Windows 與 macOS");
        }

        return services;
    }
}
