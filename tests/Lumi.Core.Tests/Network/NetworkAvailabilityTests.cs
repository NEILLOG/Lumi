using Lumi.Core.Network;

namespace Lumi.Core.Tests.Network;

public class NetworkAvailabilityTests
{
    private static NetworkAdapterInfo Up(string name, string description = "") => new(name, description, IsUp: true, IsLoopback: false);

    [Fact]
    public void AnUpPhysicalAdapter_IsUsable()
    {
        Assert.True(NetworkAvailability.HasUsableAdapter([Up("en0", "Wi-Fi")]));
        Assert.True(NetworkAvailability.HasUsableAdapter([Up("Ethernet", "Intel(R) Ethernet Connection")]));
    }

    [Fact]
    public void NoAdapters_MeansNoUsableNetwork()
    {
        Assert.False(NetworkAvailability.HasUsableAdapter([]));
    }

    [Fact]
    public void DownAdapters_AreNotUsable()
    {
        Assert.False(NetworkAvailability.HasUsableAdapter([new NetworkAdapterInfo("en0", "Wi-Fi", IsUp: false, IsLoopback: false)]));
    }

    [Fact]
    public void Loopback_IsNotUsable_EvenWhenUp()
    {
        Assert.False(NetworkAvailability.HasUsableAdapter([new NetworkAdapterInfo("lo0", "loopback", IsUp: true, IsLoopback: true)]));
    }

    [Theory]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter")]
    [InlineData("VMware Network Adapter VMnet8", "VMware Virtual Ethernet Adapter")]
    [InlineData("vmnet1", "")]
    [InlineData("docker0", "")]
    [InlineData("veth3a2", "")]
    [InlineData("bridge100", "")]
    [InlineData("awdl0", "")]          // Apple 點對點，不是上網的介面
    [InlineData("llw0", "")]
    [InlineData("anpi0", "")]
    [InlineData("WSL", "Hyper-V Virtual Ethernet")]
    public void VirtualAdapters_DoNotCountAsANetwork(string name, string description)
    {
        Assert.False(NetworkAvailability.HasUsableAdapter([Up(name, description)]));
    }

    [Fact]
    public void AVirtualAdapterPlusAPhysicalOne_IsUsable()
    {
        Assert.True(NetworkAvailability.HasUsableAdapter([Up("vEthernet (WSL)"), Up("Wi-Fi", "Intel Wi-Fi 6")]));
    }

    [Fact]
    public void VpnTunnel_CountsAsUsable()
    {
        // 隧道介面啟動代表有網路（例如 macOS 的 utun）
        Assert.True(NetworkAvailability.HasUsableAdapter([Up("utun3", "")]));
    }

    [Fact]
    public void SystemNetworkStatus_DoesNotThrow()
    {
        // 只確認可以呼叫（結果依執行環境而定，不做斷言）
        _ = new SystemNetworkStatus().HasUsableNetworkInterface();
    }
}
