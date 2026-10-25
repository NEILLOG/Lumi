using Lumi.Core.Web;

namespace Lumi.Tools.Tests.Web;

/// <summary>記錄診斷的目標，回傳預先設定的結果。</summary>
internal sealed class FakeNetworkDiagnostics : INetworkDiagnostics
{
    public List<Uri> Targets { get; } = [];

    public Func<Uri, NetworkCheckResult> Respond { get; set; } = Healthy;

    public Task<NetworkCheckResult> CheckAsync(Uri target, CancellationToken ct)
    {
        Targets.Add(target);
        return Task.FromResult(Respond(target));
    }

    /// <summary>直接連線、DNS 成功、TCP 與 HTTP 成功、有憑證。</summary>
    public static NetworkCheckResult Healthy(Uri target) => new(
        target,
        new ProxyDecision(ProxyMode.System, null, null),
        ["203.0.113.5"],
        null,
        "203.0.113.5:443",
        12,
        null,
        200,
        85,
        null,
        new CertificateInfo("CN=example.com", "CN=Public CA", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));
}
