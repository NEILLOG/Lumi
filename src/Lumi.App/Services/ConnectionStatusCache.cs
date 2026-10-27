using Lumi.Core.Chat;
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

    /// <summary>
    /// 對話中的實際結果（回應成功／失敗）：只改狀態，保留測試連線留下的可用模型清單。
    /// </summary>
    public void Mark(string connectionId, ConnectionStatus status)
    {
        var existing = Get(connectionId);
        if (existing.Status == status)
            return;
        _entries[connectionId] = existing with { Status = status };
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 一次回應失敗後，連線狀態該變成什麼；null 表示不改。
    /// 網路不通、伺服器錯誤、過載、串流中斷、金鑰無效、沒權限、找不到模型 → 連線失敗；
    /// 被限速、被拒絕、請求格式錯誤、缺金鑰、對話太長（<see cref="ChatFailureKind.ContextTooLong"/>）等代表連線本身是通的或根本沒送出去 → 不改。
    /// </summary>
    public static ConnectionStatus? StatusAfterFailure(ChatFailureKind kind) => kind switch
    {
        ChatFailureKind.ContextTooLong => null,
        ChatFailureKind.Network or ChatFailureKind.ServerError or ChatFailureKind.Overloaded
            or ChatFailureKind.StreamError or ChatFailureKind.InvalidApiKey
            or ChatFailureKind.PermissionDenied or ChatFailureKind.ModelNotFound => ConnectionStatus.Failed,
        _ => null,
    };

    /// <summary>連線的設定改了或刪除了，舊的結果就不準了。</summary>
    public void Remove(string connectionId)
    {
        if (_entries.Remove(connectionId))
            Changed?.Invoke(this, EventArgs.Empty);
    }
}
