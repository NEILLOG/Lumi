using Lumi.Core.Models;

namespace Lumi.App.Services;

public enum ConnectionStatus { Unknown, Ok, Failed }

/// <summary>
/// 最近一次「測試連線」的結果，只存在記憶體（不寫檔）：連線清單的狀態點、
/// 模型設定頁的可用模型清單都用它。
/// </summary>
public sealed class ConnectionStatusCache
{
    public sealed record Entry(ConnectionStatus Status, IReadOnlyList<string> ModelIds);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public Entry Get(string connectionId) =>
        _entries.TryGetValue(connectionId, out var entry) ? entry : new Entry(ConnectionStatus.Unknown, []);

    public void Set(string connectionId, ConnectionTestResult result)
    {
        _entries[connectionId] = new Entry(result.Success ? ConnectionStatus.Ok : ConnectionStatus.Failed, result.ModelIds);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>連線的設定改了或刪除了，舊的結果就不準了。</summary>
    public void Remove(string connectionId)
    {
        if (_entries.Remove(connectionId))
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
