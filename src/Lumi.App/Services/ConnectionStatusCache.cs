using Lumi.Core.Chat;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

public enum ConnectionStatus { Unknown, Ok, Failed }

/// <summary>
/// 最近一次「測試連線」或對話的結果：連線清單的狀態點、模型設定頁的可用模型清單都用它。
/// 狀態會存進連線的 <see cref="ConnectionSettings.LastTest"/>，重開 Lumi 後讀回來（可用模型清單不存，要列清單時照舊重新測試）。
/// </summary>
public sealed class ConnectionStatusCache
{
    public sealed record Entry(ConnectionStatus Status, IReadOnlyList<string> ModelIds);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly IModelCatalog? _catalog;
    private readonly TimeProvider _time;
    private readonly ILogger<ConnectionStatusCache>? _logger;

    /// <param name="catalog">有的話，結果會寫進連線的 <see cref="ConnectionSettings.LastTest"/>，啟動時也從那裡讀回；沒有就只存在記憶體（測試用）。</param>
    public ConnectionStatusCache(IModelCatalog? catalog = null, TimeProvider? time = null, ILogger<ConnectionStatusCache>? logger = null)
    {
        _catalog = catalog;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        if (catalog is null)
            return;

        foreach (var connection in catalog.Current.Connections)
        {
            if (connection.LastTest is { } test)
                _entries[connection.Id] = new Entry(test.Success ? ConnectionStatus.Ok : ConnectionStatus.Failed, []);
        }
    }

    public event EventHandler? Changed;

    public Entry Get(string connectionId) =>
        _entries.TryGetValue(connectionId, out var entry) ? entry : new Entry(ConnectionStatus.Unknown, []);

    public void Set(string connectionId, ConnectionTestResult result)
    {
        _entries[connectionId] = new Entry(result.Success ? ConnectionStatus.Ok : ConnectionStatus.Failed, result.ModelIds);
        Persist(connectionId, new ConnectionTestRecord(result.Success, _time.GetUtcNow()));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 對話中的實際結果（回應成功／失敗）：只改狀態，保留測試連線留下的可用模型清單。狀態沒變就不寫檔（每一回合都會呼叫）。
    /// </summary>
    public void Mark(string connectionId, ConnectionStatus status)
    {
        var existing = Get(connectionId);
        if (existing.Status == status)
            return;
        _entries[connectionId] = existing with { Status = status };
        Persist(connectionId, new ConnectionTestRecord(status == ConnectionStatus.Ok, _time.GetUtcNow()));
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

    /// <summary>連線的 URL、格式或金鑰改了，或刪除了，舊的結果就不準了。</summary>
    public void Remove(string connectionId)
    {
        if (!_entries.Remove(connectionId))
            return;
        Persist(connectionId, null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>寫進目錄是「發出去就不等」：記憶體裡的狀態已經是新的，寫檔失敗只記 log。</summary>
    private void Persist(string connectionId, ConnectionTestRecord? test)
    {
        if (_catalog is null)
            return;
        _ = SaveAsync(_catalog, connectionId, test);
    }

    private async Task SaveAsync(IModelCatalog catalog, string connectionId, ConnectionTestRecord? test)
    {
        try
        {
            await catalog.SetConnectionTestAsync(connectionId, test);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "儲存連線 {ConnectionId} 的測試結果失敗", connectionId);
        }
    }
}
