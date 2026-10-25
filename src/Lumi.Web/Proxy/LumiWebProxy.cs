using System.Net;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;

namespace Lumi.Web.Proxy;

/// <summary>
/// Lumi 所有 HttpClient 共用的 <see cref="IWebProxy"/>（M17 D2）。設定頁改了網路設定，下一個請求就用新的判斷；
/// 只有 <see cref="AppSettings.Network"/> 改變才重新建立（改主題等設定不會重新下載 PAC）。
/// </summary>
public sealed class LumiWebProxy : IWebProxy, INetworkProxyInfo, IDisposable
{
    private static readonly TimeSpan StateWait = TimeSpan.FromSeconds(10);

    private readonly IModelCatalog _catalog;
    private readonly ISecretStore _secrets;
    private readonly ILogger<LumiWebProxy> _logger;
    private readonly IWebProxy _systemProxy;
    private readonly PacScriptLoader _loader;
    private readonly object _gate = new();
    private NetworkSettings _network;
    private Task<ProxyState> _state;
    private bool _disposed;

    /// <param name="catalog">提供目前的網路設定與變更通知。</param>
    /// <param name="secrets">Auth=Account 時讀 Proxy 密碼。</param>
    public LumiWebProxy(IModelCatalog catalog, ISecretStore secrets, ILogger<LumiWebProxy> logger)
        : this(catalog, secrets, logger, null, null)
    {
    }

    // 注意：「系統 Proxy」與 PAC 載入器只給測試用的內部建構式。
    // 不能做成公開建構式的選用參數：DI 會把已註冊的 IWebProxy（就是這個類別自己）塞進來，變成循環相依。
    internal LumiWebProxy(IModelCatalog catalog, ISecretStore secrets, ILogger<LumiWebProxy> logger,
        IWebProxy? systemProxy, PacScriptLoader? loader)
    {
        _catalog = catalog;
        _secrets = secrets;
        _logger = logger;
        _systemProxy = systemProxy ?? HttpClient.DefaultProxy;
        _loader = loader ?? new PacScriptLoader();
        _network = catalog.Current.Network;
        _state = BuildAsync(_network);
        _catalog.Changed += OnChanged;
    }

    /// <summary>等目前的設定（包含 PAC 下載）處理完。</summary>
    public Task InitializeAsync(CancellationToken ct)
    {
        Task<ProxyState> state;
        lock (_gate) state = _state;
        return state.WaitAsync(ct);
    }

    public ICredentials? Credentials
    {
        get => GetState()?.Credentials;
        set { /* 驗證方式由網路設定決定，不接受外部覆寫 */ }
    }

    public Uri? GetProxy(Uri destination) => Decide(destination).Proxy;

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    public ProxyDecision Decide(Uri target)
    {
        var state = GetState();
        return state is null
            ? new ProxyDecision(_catalog.Current.Network.Mode, null, "Proxy 設定還沒有載入完成，暫時直接連線。")
            : state.Decide(target);
    }

    public void Dispose()
    {
        _catalog.Changed -= OnChanged;
        Task<ProxyState> state;
        lock (_gate)
        {
            _disposed = true;
            state = _state;
        }
        if (state.IsCompletedSuccessfully) state.Result.Dispose();
    }

    private void OnChanged(object? sender, AppSettings settings)
    {
        lock (_gate)
        {
            if (_disposed || NetworkEquals(_network, settings.Network)) return;
            _network = settings.Network;
            _state = BuildAsync(_network);
        }
    }

    private static bool NetworkEquals(NetworkSettings a, NetworkSettings b) =>
        a == b && a.BypassList.SequenceEqual(b.BypassList) && a.IntegratedAuthSites.SequenceEqual(b.IntegratedAuthSites);

    private ProxyState? GetState()
    {
        Task<ProxyState> task;
        lock (_gate) task = _state;
        try
        {
            if (task.IsCompleted) return task.Result;
            if (task.Wait(StateWait)) return task.Result;
            _logger.LogWarning("等候 Proxy 設定超過 {Seconds} 秒，暫時直接連線", StateWait.TotalSeconds);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "取得 Proxy 設定失敗，暫時直接連線");
        }
        return null;
    }

    private async Task<ProxyState> BuildAsync(NetworkSettings network)
    {
        SecretValue? password = null;
        if (network.Mode != ProxyMode.None && network.Auth == ProxyAuthMode.Account)
        {
            try
            {
                password = await _secrets.GetAsync(NetworkSecrets.ProxyPassword).ConfigureAwait(false);
            }
            catch (SecretStoreException e)
            {
                _logger.LogWarning(e, "讀不出 Proxy 密碼，這次不送出帳號驗證");
            }
        }
        return await ProxyState.CreateAsync(network, password, _loader, _systemProxy, CancellationToken.None, _logger).ConfigureAwait(false);
    }
}
