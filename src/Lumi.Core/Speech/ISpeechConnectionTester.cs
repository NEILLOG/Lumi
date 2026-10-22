using Lumi.Core.Secrets;

namespace Lumi.Core.Speech;

/// <summary>測試語音服務連線：送一小段靜音，只確認服務接受請求與 Token，不檢查辨識內容。</summary>
public interface ISpeechConnectionTester
{
    /// <param name="settings">設定頁上還沒儲存的值；不讀 <see cref="Models.IModelCatalog"/>。</param>
    /// <param name="token">設定頁上輸入的 Token；不會寫進 <see cref="Secrets.ISecretStore"/>。</param>
    /// <returns>成功正常返回；失敗丟 <see cref="SpeechFailure"/>。</returns>
    Task TestAsync(SpeechSettings settings, SecretValue token, CancellationToken ct);
}
