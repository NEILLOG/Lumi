using System.Net;
using Lumi.Core.Secrets;
using Lumi.Core.Web;
using Microsoft.Extensions.Logging;

namespace Lumi.Web.Proxy;

/// <summary>
/// 某一份網路設定算出來的 Proxy 判斷（不可變；換設定就換一份新的）。
/// 四種模式的 <see cref="Decide"/>、PAC 結果快取（5 分鐘、500 筆）與驗證用的 <see cref="Credentials"/> 都在這裡。
/// </summary>
internal sealed class ProxyState : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private const int CacheLimit = 500;

    private readonly NetworkSettings _settings;
    private readonly Uri? _manualProxy;
    private readonly PacEngine? _pac;
    private readonly IWebProxy _systemProxy;
    private readonly ILogger? _logger;
    private readonly Dictionary<(string Scheme, string Host), (ProxyDecision Decision, long ExpiresAt)> _cache = [];
    private readonly object _cacheLock = new();

    private ProxyState(NetworkSettings settings, Uri? manualProxy, PacEngine? pac, string? problem,
        ICredentials? credentials, IWebProxy systemProxy, ILogger? logger)
    {
        _settings = settings;
        _manualProxy = manualProxy;
        _pac = pac;
        Problem = problem;
        Credentials = credentials;
        _systemProxy = systemProxy;
        _logger = logger;
    }

    public ProxyMode Mode => _settings.Mode;

    /// <summary>建立狀態時就知道的問題（PAC 檔載入失敗、手動位址解析失敗）；沒有問題時是 null。</summary>
    public string? Problem { get; }

    /// <summary>送給 Proxy 的驗證身分；不需要驗證（或不使用 Proxy）時是 null。</summary>
    public ICredentials? Credentials { get; }

    public static async Task<ProxyState> CreateAsync(NetworkSettings settings, SecretValue? password, PacScriptLoader loader,
        IWebProxy systemProxy, CancellationToken ct, ILogger? logger = null)
    {
        Uri? manual = null;
        PacEngine? pac = null;
        string? problem = null;

        switch (settings.Mode)
        {
            case ProxyMode.Manual:
                if (NetworkRules.TryParseManualProxy(settings.ManualProxy, out var parsed))
                    manual = parsed;
                else
                    problem = "手動 Proxy 位址不正確，目前直接連線。";
                break;

            case ProxyMode.Pac:
                try
                {
                    var script = await loader.LoadAsync(settings.PacUrl, ct).ConfigureAwait(false);
                    pac = new PacEngine(script, PacEngine.DefaultDnsResolve, PacEngine.DefaultMyIpAddress);
                }
                catch (PacException e)
                {
                    problem = $"{e.Message}，目前直接連線。";
                    logger?.LogWarning("PAC 設定無法使用：{Message}", e.Message);
                }
                break;
        }

        return new ProxyState(settings, manual, pac, problem, BuildCredentials(settings, password), systemProxy, logger);
    }

    public ProxyDecision Decide(Uri target)
    {
        var mode = _settings.Mode;
        if (mode == ProxyMode.None || IsLocal(target))
            return new ProxyDecision(mode, null, Problem);

        switch (mode)
        {
            case ProxyMode.Manual:
                if (_manualProxy is null || NetworkRules.HostMatches(target.Host, _settings.BypassList))
                    return new ProxyDecision(mode, null, Problem);
                return new ProxyDecision(mode, _manualProxy, null);

            case ProxyMode.Pac:
                return _pac is null ? new ProxyDecision(mode, null, Problem) : DecideByPac(target);

            default:   // System
                try
                {
                    if (_systemProxy.IsBypassed(target)) return new ProxyDecision(mode, null, null);
                    var proxy = _systemProxy.GetProxy(target);
                    return new ProxyDecision(mode, proxy is null || proxy == target ? null : proxy, null);
                }
                catch (Exception e)
                {
                    _logger?.LogWarning(e, "讀取系統 Proxy 設定失敗，改為直接連線");
                    return new ProxyDecision(mode, null, "讀取系統 Proxy 設定失敗，目前直接連線。");
                }
        }
    }

    public void Dispose() => _pac?.Dispose();

    private ProxyDecision DecideByPac(Uri target)
    {
        var key = (target.Scheme, target.IdnHost);
        var now = Environment.TickCount64;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit) && hit.ExpiresAt > now)
                return hit.Decision;
        }

        ProxyDecision decision;
        try
        {
            decision = new ProxyDecision(ProxyMode.Pac, PacResult.FirstProxy(_pac!.FindProxyForUrl(target)), null);
        }
        catch (PacException e)
        {
            // log 只寫主機，不寫路徑與查詢字串（D11）
            _logger?.LogWarning("PAC 執行失敗（{Host}）：{Message}", target.IdnHost, e.Message);
            decision = new ProxyDecision(ProxyMode.Pac, null, $"{e.Message}，目前直接連線。");
        }

        lock (_cacheLock)
        {
            if (_cache.Count >= CacheLimit) _cache.Clear();
            _cache[key] = (decision, now + (long)CacheLifetime.TotalMilliseconds);
        }
        return decision;
    }

    private static bool IsLocal(Uri target) =>
        string.Equals(target.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(target.IdnHost, out var ip) && IPAddress.IsLoopback(ip));

    private static ICredentials? BuildCredentials(NetworkSettings settings, SecretValue? password)
    {
        if (settings.Mode == ProxyMode.None) return null;
        switch (settings.Auth)
        {
            case ProxyAuthMode.Integrated:
                return CredentialCache.DefaultNetworkCredentials;

            case ProxyAuthMode.Account:
                if (password is null) return null;
                var user = settings.ProxyUser;
                var slash = user.IndexOf('\\');
                return slash > 0
                    ? new NetworkCredential(user[(slash + 1)..], password.Reveal(), user[..slash])
                    : new NetworkCredential(user, password.Reveal());

            default:
                return null;
        }
    }
}
