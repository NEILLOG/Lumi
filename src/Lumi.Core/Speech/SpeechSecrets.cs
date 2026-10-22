using Lumi.Core.Secrets;

namespace Lumi.Core.Speech;

/// <summary>語音服務 Token 在 <see cref="Secrets.ISecretStore"/> 裡的名稱（同 <c>ConnectionSecrets</c> 的格式）。</summary>
public static class SpeechSecrets
{
    public static readonly SecretKey WhisperToken = new("speech/whisper/token");
}
