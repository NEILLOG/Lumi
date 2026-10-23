using System.Net.NetworkInformation;

namespace Lumi.Core.Network;

/// <summary>
/// <see cref="INetworkStatus"/> 的系統實作：列舉網路介面後交給 <see cref="NetworkAvailability"/> 判斷。
/// 列舉失敗時回 true（不確定就不下結論，不補離線的句子）。
/// </summary>
public sealed class SystemNetworkStatus : INetworkStatus
{
    public bool HasUsableNetworkInterface()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new NetworkAdapterInfo(
                    n.Name,
                    n.Description,
                    n.OperationalStatus == OperationalStatus.Up,
                    n.NetworkInterfaceType == NetworkInterfaceType.Loopback));
            return NetworkAvailability.HasUsableAdapter(adapters);
        }
        catch (NetworkInformationException)
        {
            return true;
        }
    }
}

/// <summary>一個網路介面的必要資訊（與 BCL 型別分開，判斷邏輯才能單元測試）。</summary>
public sealed record NetworkAdapterInfo(string Name, string Description, bool IsUp, bool IsLoopback);

/// <summary>
/// 可用介面的判斷：必須是「已啟動」、不是 loopback，而且不是虛擬介面（Hyper-V、VMware、Docker、
/// Apple 的 AWDL／LLW 點對點等）。VPN 的隧道介面（utun、Tunnel）算可用：隧道啟動代表有網路。
/// 寧可少判斷離線，也不要在有網路時說「沒有網路連線」。
/// </summary>
public static class NetworkAvailability
{
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vmware", "vmnet", "vbox", "hyper-v", "vethernet", "docker", "veth", "bridge",
        "wsl", "loopback", "pseudo", "awdl", "llw", "anpi",
    ];

    public static bool HasUsableAdapter(IEnumerable<NetworkAdapterInfo> adapters) =>
        adapters.Any(IsUsable);

    public static bool IsUsable(NetworkAdapterInfo adapter)
    {
        if (!adapter.IsUp || adapter.IsLoopback)
            return false;

        var label = $"{adapter.Name} {adapter.Description}";
        return !VirtualMarkers.Any(marker => label.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
