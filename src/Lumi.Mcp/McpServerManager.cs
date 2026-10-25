using System.Text.Json;
using Lumi.Core.Mcp;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Core.Reminders;
using Microsoft.Extensions.Logging;

namespace Lumi.Mcp;

/// <summary>
/// MCP 伺服器的管理員（M16 16C）：依設定連上每個已啟用的伺服器，維護狀態與工具清單，提供 <see cref="IMcpToolSource"/> 給 ToolCatalog，並執行工具呼叫。
/// 同一個伺服器的連線與斷線用各自的 <see cref="SemaphoreSlim"/> 排隊；<see cref="Changed"/> 可能在背景執行緒觸發。
/// 程序結束時由 <see cref="DisposeAsync"/> 關閉所有連線（stdio 程序跟著 Lumi 結束，M16 D14）。
/// </summary>
public sealed class McpServerManager : IMcpToolSource, IMcpToolCaller, IAsyncDisposable
{
    /// <summary>啟動與測試連線的上限（M16 16C）。</summary>
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>工具呼叫時發現斷線，重新連線的上限（M16 D11）。</summary>
    internal static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(10);

    private readonly IModelCatalog _settings;
    private readonly IMcpConnectionFactory _factory;
    private readonly IMcpToolCache _cache;
    private readonly IShellEnvironment _shell;
    private readonly INotificationSink _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger<McpServerManager> _logger;

    /// <summary>所有狀態的鎖。事件與通知一律在鎖外觸發。</summary>
    private readonly object _sync = new();
    private readonly Dictionary<string, ServerEntry> _entries = new(StringComparer.Ordinal);
    private bool _subscribed;
    private bool _disposed;

    public McpServerManager(
        IModelCatalog settings,
        IMcpConnectionFactory factory,
        IMcpToolCache cache,
        IShellEnvironment shell,
        INotificationSink notifications,
        TimeProvider time,
        ILogger<McpServerManager> logger)
    {
        _settings = settings;
        _factory = factory;
        _cache = cache;
        _shell = shell;
        _notifications = notifications;
        _time = time;
        _logger = logger;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// 依目前設定開始連線（每個已啟用的伺服器平行進行），並訂閱設定變更。等到所有這次開始的連線結束（成功或失敗）才完成；
    /// 啟動時的 hosted service 不需要等它。
    /// </summary>
    public async Task StartAllAsync(CancellationToken ct = default)
    {
        Task[] pending;
        lock (_sync)
        {
            if (_disposed)
                return;
            if (!_subscribed)
            {
                _settings.Changed += OnSettingsChanged;
                _subscribed = true;
            }
            Sync(_settings.Current);
            pending = PendingTasks();
        }
        RaiseChanged();
        await Task.WhenAll(pending).WaitAsync(ct);
    }

    public IReadOnlyList<IMcpTool> GetTools()
    {
        lock (_sync)
        {
            var tools = new List<IMcpTool>();
            foreach (var entry in _entries.Values.Where(e => e.Settings.Enabled))
            {
                var live = IsLive(entry);
                var source = live ? entry.LiveTools : entry.CachedTools;
                tools.AddRange(source.Select(t => new McpTool(entry.Settings, t, live, _settings, this)));
            }
            return tools;
        }
    }

    public IReadOnlyList<McpServerStatus> GetStatuses()
    {
        lock (_sync)
        {
            return _entries.Values
                .Select(e => new McpServerStatus(
                    e.Settings.Id,
                    e.Settings.Enabled ? EffectiveState(e) : McpServerState.Disabled,
                    e.Settings.Enabled ? EffectiveError(e) : null,
                    IsLive(e) ? e.LiveTools.Count : e.CachedTools.Count))
                .ToList();
        }
    }

    public async Task WaitForStartupAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task[] pending;
        lock (_sync)
        {
            pending = PendingTasks();
        }
        if (pending.Length == 0)
            return;

        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout, ct));
    }

    public async Task<McpTestResult> TestAsync(McpServerSettings server, McpServerSecrets secrets, CancellationToken ct)
    {
        try
        {
            var (connection, tools) = await OpenAsync(server, secrets, ConnectTimeout, ct);
            try
            {
                return new McpTestResult(true, null, tools.Select(t => t.Name).ToList());
            }
            finally
            {
                await DisposeQuietlyAsync(connection);
            }
        }
        catch (McpFailure ex)
        {
            return new McpTestResult(false, ex.Message, []);
        }
    }

    public async Task RestartAsync(string serverId, CancellationToken ct)
    {
        Task pending;
        lock (_sync)
        {
            if (_disposed || !_entries.TryGetValue(serverId, out var entry) || !entry.Settings.Enabled)
                return;
            StartConnect(entry);
            pending = entry.Pending;
        }
        RaiseChanged();
        await pending.WaitAsync(ct);
    }

    /// <summary>
    /// 工具呼叫（M16 D11）：沒連線（或連線已斷）時先重新連線一次，最多 10 秒；仍失敗就丟 <see cref="McpCallException"/>。
    /// 引數與結果不寫 log，只記工具名稱與耗時。
    /// </summary>
    public async Task<McpCallResult> CallAsync(string serverId, string toolName, string argumentsJson, CancellationToken ct)
    {
        var entry = FindEntry(serverId) ?? throw new McpCallException("MCP 伺服器目前沒有連線。");
        var connection = await EnsureConnectedAsync(entry, ct);

        var arguments = ParseArguments(argumentsJson);
        var started = _time.GetTimestamp();
        try
        {
            var result = await connection.CallToolAsync(toolName, arguments, ct);
            _logger.LogInformation("MCP 工具 {Tool} 完成，耗時 {Ms} ms", toolName, _time.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("MCP 工具 {Tool} 失敗（{Type}）", toolName, ex.GetType().Name);
            throw new McpCallException(McpFailureText.DescribeCall(ex));
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IMcpConnection> connections = [];
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_subscribed)
                _settings.Changed -= OnSettingsChanged;
            foreach (var entry in _entries.Values)
            {
                entry.Cts?.Cancel();
                if (entry.Connection is { } connection)
                    connections.Add(connection);
                entry.Connection = null;
            }
        }

        foreach (var connection in connections)
            await DisposeQuietlyAsync(connection);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            Sync(settings);
        }
        RaiseChanged();
    }

    /// <summary>把管理員的狀態對齊設定。呼叫端要持有 <see cref="_sync"/>；不等待連線完成。</summary>
    private void Sync(AppSettings settings)
    {
        var wanted = settings.McpServers
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var id in _entries.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
            Remove(id);

        foreach (var server in wanted.Values)
        {
            if (!_entries.TryGetValue(server.Id, out var entry))
            {
                entry = new ServerEntry(server);
                _entries[server.Id] = entry;
                if (server.Enabled)
                    StartConnect(entry);
                else
                    entry.State = McpServerState.Disabled;
                continue;
            }

            var connectionChanged = ConnectionKey(entry.Settings) != ConnectionKey(server);
            entry.Settings = server;

            if (!server.Enabled)
            {
                if (entry.State != McpServerState.Disabled)
                {
                    Disconnect(entry);
                    entry.State = McpServerState.Disabled;
                    entry.Error = null;
                }
            }
            else if (entry.State == McpServerState.Disabled || connectionChanged)
            {
                StartConnect(entry);
            }
        }
    }

    private void Remove(string id)
    {
        var entry = _entries[id];
        entry.Removed = true;
        Disconnect(entry);
        _entries.Remove(id);
        _ = DeleteCacheQuietlyAsync(id);
    }

    /// <summary>開始（或重新開始）連線。呼叫端要持有 <see cref="_sync"/>。</summary>
    private void StartConnect(ServerEntry entry)
    {
        Disconnect(entry);
        entry.Cts = new CancellationTokenSource();
        entry.State = McpServerState.Connecting;
        entry.Error = null;

        var token = entry.Cts.Token;
        var server = entry.Settings;
        entry.Pending = Task.Run(() => ConnectInBackgroundAsync(entry, server, token));
    }

    /// <summary>取消進行中的連線、關掉目前的連線。呼叫端要持有 <see cref="_sync"/>。</summary>
    private void Disconnect(ServerEntry entry)
    {
        entry.Cts?.Cancel();
        if (entry.Connection is { } connection)
        {
            entry.Connection = null;
            _ = DisposeQuietlyAsync(connection);
        }
    }

    /// <summary>背景的連線：讀祕密、開連線、列工具；成功寫快取，失敗送通知。絕不往外丟例外。</summary>
    private async Task ConnectInBackgroundAsync(ServerEntry entry, McpServerSettings server, CancellationToken token)
    {
        try
        {
            await entry.Gate.WaitAsync(token);
            try
            {
                await LoadCacheIfNeededAsync(entry, server.Id, token);
                var secrets = await _settings.GetMcpSecretsAsync(server.Id, token);
                var (connection, tools) = await OpenAsync(server, secrets, ConnectTimeout, token);

                bool accepted;
                lock (_sync)
                {
                    accepted = !token.IsCancellationRequested && !entry.Removed;
                    if (accepted)
                    {
                        entry.Connection = connection;
                        entry.LiveTools = tools;
                        entry.CachedTools = tools;
                        entry.SecretValues = SecretValues(secrets);
                        entry.State = McpServerState.Ready;
                        entry.Error = null;
                    }
                }

                if (!accepted)
                {
                    await DisposeQuietlyAsync(connection);
                    return;
                }

                _logger.LogInformation("MCP 伺服器 {ServerId} 已連線，{Count} 個工具", server.Id, tools.Count);
                RaiseChanged();
                await SaveCacheQuietlyAsync(server.Id, tools);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 被取消（停用、刪除或重新連線）：舊的結果不再影響狀態
        }
        catch (Exception ex)
        {
            var message = ex is McpFailure failure ? failure.Message : McpFailureText.DescribeConnection(ex, server, []);
            await FailAsync(entry, server, token, message);
        }
    }

    private async Task FailAsync(ServerEntry entry, McpServerSettings server, CancellationToken token, string message)
    {
        lock (_sync)
        {
            if (token.IsCancellationRequested || entry.Removed)
                return;
            entry.State = McpServerState.Failed;
            entry.Error = message;
        }

        var firstLine = message.Split('\n')[0];
        _logger.LogWarning("MCP 伺服器 {ServerId} 連線失敗：{Reason}", server.Id, firstLine);
        RaiseChanged();

        try
        {
            await _notifications.ShowAsync(
                new AppNotification($"mcp-{server.Id}", "MCP 伺服器連不上", $"「{server.Name}」：{firstLine}", _time.GetUtcNow(), NotificationKind.SystemNotice),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP 伺服器 {ServerId} 的失敗通知無法送出", server.Id);
        }
    }

    /// <summary>
    /// 呼叫前確保連線可用。已連線就直接回傳；否則在伺服器的排隊裡重新連線一次（10 秒）。
    /// 失敗丟 <see cref="McpCallException"/>，訊息是固定的「目前沒有連線」。
    /// </summary>
    private async Task<IMcpConnection> EnsureConnectedAsync(ServerEntry entry, CancellationToken ct)
    {
        lock (_sync)
        {
            if (!entry.Settings.Enabled)
                throw new McpCallException($"MCP 伺服器「{entry.Settings.Name}」已停用。");
            if (entry.Connection is { IsAlive: true } alive)
                return alive;
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            McpServerSettings server;
            lock (_sync)
            {
                if (entry.Connection is { IsAlive: true } alive)
                    return alive;
                server = entry.Settings;
                if (!server.Enabled || entry.Removed)
                    throw new McpCallException($"MCP 伺服器「{server.Name}」目前沒有連線。");
                DropConnection(entry);
                entry.State = McpServerState.Connecting;
            }
            RaiseChanged();

            try
            {
                var secrets = await _settings.GetMcpSecretsAsync(server.Id, ct);
                var (connection, tools) = await OpenAsync(server, secrets, ReconnectTimeout, ct);
                lock (_sync)
                {
                    if (entry.Removed || !entry.Settings.Enabled)
                    {
                        _ = DisposeQuietlyAsync(connection);
                        throw new McpCallException($"MCP 伺服器「{server.Name}」目前沒有連線。");
                    }
                    entry.Connection = connection;
                    entry.LiveTools = tools;
                    entry.CachedTools = tools;
                    entry.SecretValues = SecretValues(secrets);
                    entry.State = McpServerState.Ready;
                    entry.Error = null;
                }
                _logger.LogInformation("MCP 伺服器 {ServerId} 已重新連線", server.Id);
                RaiseChanged();
                return connection;
            }
            catch (Exception ex) when (ex is not McpCallException and not OperationCanceledException)
            {
                var message = ex is McpFailure failure ? failure.Message : McpFailureText.DescribeConnection(ex, server, entry.SecretValues);
                lock (_sync)
                {
                    entry.State = McpServerState.Failed;
                    entry.Error = message;
                }
                _logger.LogWarning("MCP 伺服器 {ServerId} 重新連線失敗：{Reason}", server.Id, message.Split('\n')[0]);
                RaiseChanged();
                throw new McpCallException($"MCP 伺服器「{server.Name}」目前沒有連線。");
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// 開一條連線並列出工具。失敗丟 <see cref="McpFailure"/>（訊息已去掉祕密的值）；被取消時丟 <see cref="OperationCanceledException"/>。
    /// 呼叫端負責釋放回傳的連線（失敗時這裡已釋放）。
    /// </summary>
    private async Task<(IMcpConnection Connection, IReadOnlyList<McpCachedTool> Tools)> OpenAsync(
        McpServerSettings server, McpServerSecrets secrets, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            var environment = await _shell.GetAsync(limit.Token);
            var connection = await _factory.ConnectAsync(server, secrets, environment, limit.Token);
            try
            {
                var tools = await connection.ListToolsAsync(limit.Token);
                return (connection, tools);
            }
            catch
            {
                await DisposeQuietlyAsync(connection);
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new McpFailure($"連線逾時（{(int)timeout.TotalSeconds} 秒）");
        }
        catch (Exception ex) when (ex is not McpFailure)
        {
            throw new McpFailure(McpFailureText.DescribeConnection(ex, server, SecretValues(secrets)));
        }
    }

    private async Task LoadCacheIfNeededAsync(ServerEntry entry, string serverId, CancellationToken token)
    {
        if (entry.CacheLoaded)
            return;
        try
        {
            entry.CachedTools = await _cache.GetAsync(serverId, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "MCP 伺服器 {ServerId} 的工具快取無法讀取", serverId);
        }
        entry.CacheLoaded = true;
    }

    private async Task SaveCacheQuietlyAsync(string serverId, IReadOnlyList<McpCachedTool> tools)
    {
        try
        {
            await _cache.SaveAsync(serverId, tools, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP 伺服器 {ServerId} 的工具快取無法寫入", serverId);
        }
    }

    private async Task DeleteCacheQuietlyAsync(string serverId)
    {
        try
        {
            await _cache.DeleteAsync(serverId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP 伺服器 {ServerId} 的工具快取無法刪除", serverId);
        }
    }

    private async Task DisposeQuietlyAsync(IMcpConnection connection)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP 連線關閉時發生錯誤");
        }
    }

    /// <summary>丟掉連線（不取消連線的排隊）。呼叫端要持有 <see cref="_sync"/>。</summary>
    private void DropConnection(ServerEntry entry)
    {
        if (entry.Connection is { } connection)
        {
            entry.Connection = null;
            _ = DisposeQuietlyAsync(connection);
        }
    }

    private ServerEntry? FindEntry(string serverId)
    {
        lock (_sync)
        {
            return _entries.GetValueOrDefault(serverId);
        }
    }

    private Task[] PendingTasks() => _entries.Values.Select(e => e.Pending).Where(t => !t.IsCompleted).ToArray();

    private static bool IsLive(ServerEntry entry) => EffectiveState(entry) == McpServerState.Ready;

    /// <summary>Ready 但連線已經斷了（stdio 程序結束等），狀態改報失敗。</summary>
    private static McpServerState EffectiveState(ServerEntry entry) =>
        entry.State == McpServerState.Ready && entry.Connection?.IsAlive != true ? McpServerState.Failed : entry.State;

    private static string? EffectiveError(ServerEntry entry) =>
        entry.State == McpServerState.Ready && EffectiveState(entry) == McpServerState.Failed ? "連線已中斷" : entry.Error;

    /// <summary>連線相關的設定改變時才需要重新連線（名稱、代號、逾時、是否在小精靈用都不用）。</summary>
    private static string ConnectionKey(McpServerSettings s) => string.Join(
        '\u0001',
        s.Transport,
        s.Command,
        string.Join('\u0002', s.Args),
        s.WorkingDirectory,
        string.Join('\u0002', s.EnvNames),
        s.Url,
        string.Join('\u0002', s.HeaderNames),
        s.SecretsVersion,
        s.Enabled);

    private static string[] SecretValues(McpServerSecrets secrets) =>
        secrets.Env.Values.Concat(secrets.Headers.Values).ToArray();

    private static JsonElement ParseArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new McpCallException("參數不是有效的 JSON。");
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>一個伺服器的執行期狀態。所有欄位都由 <see cref="_sync"/> 保護（<see cref="Gate"/> 只排隊連線與斷線）。</summary>
    private sealed class ServerEntry(McpServerSettings settings)
    {
        public McpServerSettings Settings { get; set; } = settings;

        public McpServerState State { get; set; } = McpServerState.Disabled;

        public string? Error { get; set; }

        public IMcpConnection? Connection { get; set; }

        public IReadOnlyList<McpCachedTool> LiveTools { get; set; } = [];

        public IReadOnlyList<McpCachedTool> CachedTools { get; set; } = [];

        public bool CacheLoaded { get; set; }

        public string[] SecretValues { get; set; } = [];

        public bool Removed { get; set; }

        /// <summary>目前這次連線的取消來源；換新或停用時取消。</summary>
        public CancellationTokenSource? Cts { get; set; }

        /// <summary>目前這次開始的連線（不會丟例外）。</summary>
        public Task Pending { get; set; } = Task.CompletedTask;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
