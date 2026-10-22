using Lumi.Core.Secrets;
using Lumi.Core.Speech;

namespace Lumi.App.Tests.TestSupport;

/// <summary>假的語音測試連線：記錄每次呼叫的設定與 Token（明文，只在測試裡），可指定成功或失敗，也可用 Gate 停住測試中的狀態。</summary>
internal sealed class FakeSpeechConnectionTester : ISpeechConnectionTester
{
    /// <summary>null 表示成功；不為 null 時丟這個失敗。</summary>
    public SpeechFailure? Failure { get; set; }

    /// <summary>不為 null 時，測試會等到這個工作完成（用來測「測試中」的狀態）。</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(SpeechSettings Settings, string Token)> Calls { get; } = [];

    public async Task TestAsync(SpeechSettings settings, SecretValue token, CancellationToken ct)
    {
        Calls.Add((settings, token.Reveal()));
        if (Gate is { } gate)
            await gate.Task.WaitAsync(ct);
        if (Failure is { } failure)
            throw failure;
    }
}
