namespace Lumi.Core.Secrets;

/// <summary>
/// 金鑰存在但讀寫失敗（解密失敗、鑰匙圈被鎖、使用者拒絕授權…）。
/// 訊息不得包含金鑰內容。UI 顯示「金鑰無法讀取，請重新輸入」。
/// </summary>
public class SecretStoreException : Exception
{
    public SecretStoreException(string message) : base(message) { }

    public SecretStoreException(string message, Exception innerException) : base(message, innerException) { }
}
