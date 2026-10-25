using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jint;

namespace Lumi.Web.Proxy;

/// <summary>PAC 檔的錯誤（語法、執行、逾時）。訊息可以直接給使用者看。</summary>
internal sealed class PacException(string message) : Exception(message);

/// <summary>
/// 用 Jint 執行 PAC 檔。沙盒：2 秒逾時、16 MB 記憶體、100 萬個陳述式；PAC 只能呼叫標準輔助函式與 C# 提供的
/// <c>dnsResolve</c>、<c>myIpAddress</c>。Jint 的 <see cref="Engine"/> 不是執行緒安全，所以呼叫一律上鎖。
/// </summary>
internal sealed class PacEngine : IDisposable
{
    private const string ResourceName = "Lumi.Web.Proxy.PacHelpers.js";

    private static readonly Lazy<string> HelperScript = new(LoadHelperScript);

    private readonly Engine _engine;
    private readonly object _lock = new();

    /// <summary>PAC 標準輔助函式的原始碼：先找內嵌資源，找不到退回讀 <c>AppContext.BaseDirectory/Proxy/PacHelpers.js</c>（測試用）。</summary>
    internal static string Script => HelperScript.Value;

    public PacEngine(string pacScript, Func<string, string?> dnsResolve, Func<string> myIpAddress)
    {
        _engine = new Engine(o => o
            .TimeoutInterval(TimeSpan.FromSeconds(2))
            .LimitMemory(16_000_000)
            .MaxStatements(1_000_000));
        try
        {
            _engine.SetValue("dnsResolve", dnsResolve);
            _engine.SetValue("myIpAddress", myIpAddress);
            _engine.Execute(Script);
            _engine.Execute(pacScript);
        }
        catch (Exception e)
        {
            _engine.Dispose();
            throw new PacException($"PAC 檔有錯誤：{e.Message}");
        }
    }

    /// <summary>
    /// 執行 <c>FindProxyForURL</c>。https 只給 <c>https://{host}/</c>（不洩漏路徑，同 Chrome 的做法），
    /// http 給完整網址（不含 <c>#</c>）。
    /// </summary>
    public string FindProxyForUrl(Uri url)
    {
        var host = url.IdnHost;
        var arg = url.Scheme == Uri.UriSchemeHttps
            ? $"https://{host}/"
            : url.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
        try
        {
            lock (_lock)
            {
                var value = _engine.Invoke("FindProxyForURL", arg, host);
                if (!value.IsString())
                    throw new PacException("PAC 檔有錯誤：FindProxyForURL 沒有回傳字串");
                return value.AsString();
            }
        }
        catch (PacException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new PacException($"PAC 檔有錯誤：{e.Message}");
        }
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>預設的 DNS 查詢：最多等 2 秒，取第一個 IPv4；失敗回 null。</summary>
    internal static string? DefaultDnsResolve(string host)
    {
        try
        {
            var task = Dns.GetHostAddressesAsync(host);
            if (!task.Wait(TimeSpan.FromSeconds(2))) return null;
            return task.Result.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>第一個狀態是 Up、非迴路的 IPv4；沒有時 <c>127.0.0.1</c>。</summary>
    internal static string DefaultMyIpAddress()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }
        catch
        {
            // 取不到網卡資訊時用迴路位址
        }
        return "127.0.0.1";
    }

    private static string LoadHelperScript()
    {
        using (var stream = typeof(PacEngine).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Proxy", "PacHelpers.js");
        if (File.Exists(path))
            return File.ReadAllText(path);

        throw new InvalidOperationException($"找不到 PAC 輔助函式資源 {ResourceName}。");
    }
}
