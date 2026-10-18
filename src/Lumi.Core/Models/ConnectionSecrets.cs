using Lumi.Core.Secrets;

namespace Lumi.Core.Models;

/// <summary>連線金鑰在 <see cref="ISecretStore"/> 裡的名稱，沿用 1B 的格式。</summary>
public static class ConnectionSecrets
{
    public static SecretKey ApiKey(string connectionId) => new($"connection/{connectionId}/api-key");

    /// <summary>給設定頁顯示用的末 4 碼。</summary>
    public static string Hint(SecretValue apiKey)
    {
        var plain = apiKey.Reveal();
        return plain.Length <= 4 ? plain : plain[^4..];
    }
}
