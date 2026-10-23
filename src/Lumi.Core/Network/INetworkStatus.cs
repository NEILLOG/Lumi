namespace Lumi.Core.Network;

/// <summary>
/// 系統有沒有可用的網路介面，只用在「失敗之後」補說明文字（M9 9C）。不做主動偵測、不阻擋送出：
/// VPN、內網端點與本機模型（localhost）下這個判斷不可靠。
/// </summary>
public interface INetworkStatus
{
    bool HasUsableNetworkInterface();
}

/// <summary>測試與沒有接上系統資訊時用：一律當作有網路（不補離線的句子）。</summary>
public sealed class AlwaysOnlineNetworkStatus : INetworkStatus
{
    public bool HasUsableNetworkInterface() => true;
}
