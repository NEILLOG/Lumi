using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Lumi.Platform.Secrets;

/// <summary>
/// Windows：DPAPI（目前使用者範圍）加密後存在 <c>%APPDATA%\Lumi\secrets.json</c>。
/// 已知限制：Windows 密碼被管理員重設（不是使用者自己改）時，DPAPI 金鑰會失效，
/// 讀取會解密失敗，丟 <see cref="Lumi.Core.Secrets.SecretStoreException"/>，請使用者重新輸入。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretStore : EncryptedFileSecretStore
{
    public WindowsDpapiSecretStore(IOptions<SecretStoreOptions> options)
        : base(options.Value.ResolveDpapiFilePath(), new DpapiProtector())
    {
    }

    [SupportedOSPlatform("windows")]
    private sealed class DpapiProtector : ISecretProtector
    {
        // 不是祕密：只是把 Lumi 的資料和其他程式的 DPAPI 資料區隔開
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("io.github.neillog.lumi/secrets/v1");

        public byte[] Protect(byte[] plain) =>
            ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        public byte[] Unprotect(byte[] protectedData) =>
            ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}
