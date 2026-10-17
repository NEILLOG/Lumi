using System.Security.Cryptography;
using Lumi.Platform.Secrets;

namespace Lumi.Platform.Tests;

/// <summary>測試用：加上檔頭並 XOR；檔頭不對就丟 <see cref="CryptographicException"/>，模擬 DPAPI 解密失敗。</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private static readonly byte[] Header = [0xA5, 0x5A, 0x01];
    private const byte Mask = 0x6B;

    public byte[] Protect(byte[] plain) =>
        [.. Header, .. plain.Select(b => (byte)(b ^ Mask))];

    public byte[] Unprotect(byte[] protectedData)
    {
        if (protectedData.Length < Header.Length || !protectedData.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new CryptographicException("資料不是這個使用者加密的");
        return protectedData.Skip(Header.Length).Select(b => (byte)(b ^ Mask)).ToArray();
    }
}
