using Lumi.Core.Chat;
using Lumi.Core.Secrets;

namespace Lumi.Core.Models;

/// <summary>「測試連線」的結果：成功時帶可用的模型 ID；失敗時帶原因（Kind 分得出金鑰錯、網址錯、服務沒開）。</summary>
public sealed record ConnectionTestResult(IReadOnlyList<string> ModelIds, ChatFailure? Failure)
{
    public bool Success => Failure is null;

    public static ConnectionTestResult Ok(IReadOnlyList<string> modelIds) => new(modelIds, null);

    public static ConnectionTestResult Fail(ChatFailure failure) => new([], failure);
}

/// <summary>測試連線＝列出這個連線可用的模型。依連線格式分派給對應的實作。</summary>
public interface IConnectionTester
{
    /// <param name="apiKey">表單上還沒儲存的金鑰；null 表示改用已儲存的金鑰（沒有也行，例如 Ollama）。不會寫進 <see cref="ISecretStore"/>。</param>
    Task<ConnectionTestResult> TestAsync(ConnectionSettings connection, SecretValue? apiKey, CancellationToken ct = default);
}
