using System.Diagnostics;
using System.Net;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;

namespace Lumi.Web.Proxy;

/// <summary>設定頁的「測試連線」：用尚未儲存的設定連到指定網址，回一句給使用者看的結果。</summary>
public sealed class NetworkTester : INetworkTester
{
    private const int TimeoutSeconds = 15;

    private readonly ISecretStore _secrets;
    private readonly ILogger<NetworkTester> _logger;
    private readonly IWebProxy _systemProxy;
    private readonly PacScriptLoader _loader;

    public NetworkTester(ISecretStore secrets, ILogger<NetworkTester> logger)
        : this(secrets, logger, null, null)
    {
    }

    // 與 LumiWebProxy 相同：系統 Proxy 只給測試用的內部建構式，避免 DI 把 LumiWebProxy 當成「系統 Proxy」塞進來。
    internal NetworkTester(ISecretStore secrets, ILogger<NetworkTester> logger, IWebProxy? systemProxy, PacScriptLoader? loader)
    {
        _secrets = secrets;
        _logger = logger;
        _systemProxy = systemProxy ?? HttpClient.DefaultProxy;
        _loader = loader ?? new PacScriptLoader();
    }

    public async Task<NetworkTestResult> TestAsync(NetworkSettings draft, SecretValue? password, Uri target, CancellationToken ct)
    {
        var settings = draft.Normalize();
        try
        {
            if (password is null && settings.Mode != ProxyMode.None && settings.Auth == ProxyAuthMode.Account)
            {
                try
                {
                    password = await _secrets.GetAsync(NetworkSecrets.ProxyPassword, ct).ConfigureAwait(false);
                }
                catch (SecretStoreException e)
                {
                    _logger.LogWarning(e, "讀不出已儲存的 Proxy 密碼");
                    return new NetworkTestResult(false, "讀不出已儲存的 Proxy 密碼，請重新輸入。");
                }
            }

            using var state = await ProxyState.CreateAsync(settings, password, _loader, _systemProxy, ct, _logger).ConfigureAwait(false);
            if (state.Problem is { } problem)
                return new NetworkTestResult(false, problem);

            using var handler = new SocketsHttpHandler
            {
                Proxy = new StateProxy(state),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AllowAutoRedirect = true,
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            var decision = state.Decide(target);
            var watch = Stopwatch.StartNew();
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            watch.Stop();

            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
                return new NetworkTestResult(false, WebFailures.ProxyAuthentication().Message);

            var via = decision.Proxy is { } p ? $"經過 Proxy {p.Host}:{p.Port}" : "直接連線";
            return new NetworkTestResult(true, $"連線成功（HTTP {(int)response.StatusCode}，{via}，{watch.ElapsedMilliseconds} 毫秒）。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // log 只寫主機（D11）
            _logger.LogInformation("測試連線失敗（{Host}）：{Type}", target.IdnHost, e.GetType().Name);
            return new NetworkTestResult(false, WebFailures.From(e, TimeoutSeconds).Message);
        }
    }

    /// <summary>把一個 <see cref="ProxyState"/> 包成 <see cref="IWebProxy"/>。</summary>
    private sealed class StateProxy(ProxyState state) : IWebProxy
    {
        public ICredentials? Credentials { get => state.Credentials; set { } }

        public Uri? GetProxy(Uri destination) => state.Decide(destination).Proxy;

        public bool IsBypassed(Uri host) => GetProxy(host) is null;
    }
}
