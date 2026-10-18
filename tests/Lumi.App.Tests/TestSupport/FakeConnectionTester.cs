using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Secrets;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeConnectionTester : IConnectionTester
{
    public ConnectionTestResult Result { get; set; } = ConnectionTestResult.Ok(["model-a", "model-b"]);

    /// <summary>不為 null 時，測試會等到這個工作完成（用來測取消與測試中的狀態）。</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(ConnectionSettings Connection, string? Key)> Calls { get; } = [];

    public async Task<ConnectionTestResult> TestAsync(ConnectionSettings connection, SecretValue? apiKey, CancellationToken ct = default)
    {
        Calls.Add((connection, apiKey?.Reveal()));
        if (Gate is { } gate)
            await gate.Task.WaitAsync(ct);
        return Result;
    }

    public static ConnectionTestResult Failure(ChatFailureKind kind, string message) =>
        ConnectionTestResult.Fail(new ChatFailure(kind, message, false));
}
