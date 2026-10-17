namespace Lumi.Core.Secrets;

/// <summary>
/// 金鑰與 Token 的安全儲存（Windows DPAPI／macOS Keychain）。金鑰只能存在這裡，
/// 不能出現在設定檔、SQLite、log 或例外訊息（agent.md 第 10 節）。
/// </summary>
/// <remarks>
/// 介面是非同步的：Keychain 呼叫可能跳出系統授權視窗而卡住，不能在 UI 執行緒上同步等。
/// 「沒有這個金鑰」回傳 null／false；「有但讀不出來」（解密失敗、鑰匙圈被鎖、使用者拒絕授權）丟 <see cref="SecretStoreException"/>。
/// </remarks>
public interface ISecretStore
{
    Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default);

    Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default);

    /// <returns>刪除成功回 true；原本就不存在回 false。</returns>
    Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default);

    Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default);
}
