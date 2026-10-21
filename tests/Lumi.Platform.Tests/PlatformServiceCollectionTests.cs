using Lumi.Core.Platform;
using Lumi.Core.Secrets;
using Lumi.Platform.Overlay;
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

    [SkippableFact]
    public void AddLumiPlatform_registers_a_new_overlay_per_window_for_this_os()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "只支援 Windows 與 macOS");
        var services = new ServiceCollection();
        services.AddLumiPlatform(o => o.KeychainService = "io.github.neillog.lumi.tests");

        using var provider = services.BuildServiceProvider(validateScopes: true);
        var first = provider.GetRequiredService<IOverlayPlatform>();
        var second = provider.GetRequiredService<IOverlayPlatform>();

        Assert.NotSame(first, second);   // 每個視窗各用一個
        Assert.Equal(OperatingSystem.IsMacOS() ? nameof(MacOverlay) : nameof(WindowsOverlay), first.GetType().Name);
        Assert.False(first.IsSupported);   // 還沒 Attach
        Assert.False(first.TryGetCursorInWindow(1, 100, out _));
    }

    [SkippableFact]
    public void Overlay_AttachWithoutAHandle_StaysUnsupported_AndNeverThrows()
    {
        Skip.IfNot(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), "只支援 Windows 與 macOS");
        var services = new ServiceCollection();
        services.AddLumiPlatform(o => o.KeychainService = "io.github.neillog.lumi.tests");
        using var provider = services.BuildServiceProvider();
        var overlay = provider.GetRequiredService<IOverlayPlatform>();

        overlay.Attach(0);
        overlay.SetClickThrough(true);   // 沒有視窗：什麼都不發生

        Assert.False(overlay.IsSupported);
        Assert.False(overlay.ClickThroughActive);
    }

    [Fact]
    public void Overlay_Layered_IsOnByDefault()
    {
        Assert.True(new OverlayOptions().Layered);
    }
}
