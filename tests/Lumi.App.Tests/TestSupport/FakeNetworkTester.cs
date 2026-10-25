using Lumi.Core.Secrets;
using Lumi.Core.Web;

namespace Lumi.App.Tests.TestSupport;

/// <summary>假的網路測試：記錄每次呼叫的草稿、密碼（明文，只在測試裡）與目標網址，回傳指定的結果，也可用 Gate 停住測試中的狀態。</summary>
internal sealed class FakeNetworkTester : INetworkTester
{
    /// <summary>每次回傳的結果。</summary>
    public NetworkTestResult Result { get; set; } = new(true, "連線成功");

    /// <summary>不為 null 時，測試會等到這個工作完成（用來測「測試中」的狀態）。</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(NetworkSettings Draft, string? Password, Uri Target)> Calls { get; } = [];

    public async Task<NetworkTestResult> TestAsync(NetworkSettings draft, SecretValue? password, Uri target, CancellationToken ct)
    {
        Calls.Add((draft, password?.Reveal(), target));
        if (Gate is { } gate)
            await gate.Task.WaitAsync(ct);
        return Result;
    }
}
