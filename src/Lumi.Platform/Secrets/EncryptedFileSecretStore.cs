using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumi.Core.IO;
using Lumi.Core.Secrets;

namespace Lumi.Platform.Secrets;

/// <summary>把明文轉成只有本機這個使用者解得開的位元組（Windows 為 DPAPI）。</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);

    /// <exception cref="CryptographicException">資料損毀，或不是這個使用者加密的。</exception>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// 加密後存成 JSON 檔：<c>{ "version": 1, "items": { "key-name": "base64…" } }</c>。
/// 檔案內只有加密後的 base64，沒有明文。
/// </summary>
/// <remarks>
/// 寫入用 <see cref="AtomicFile"/>（先寫暫存檔再取代）；同一程序內以 <see cref="SemaphoreSlim"/> 序列化。
/// 每次都從檔案讀，不另外快取，避免明文長時間留在記憶體。
/// 加密的部分由 <see cref="ISecretProtector"/> 提供，所以檔案處理的邏輯在非 Windows 上也能測。
/// </remarks>
public class EncryptedFileSecretStore : ISecretStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public EncryptedFileSecretStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public async Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        var name = key.RequireName();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(ct).ConfigureAwait(false);
            if (!file.Items.TryGetValue(name, out var encoded))
                return null;

            try
            {
                var plain = _protector.Unprotect(Convert.FromBase64String(encoded));
                return new SecretValue(Encoding.UTF8.GetString(plain));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                throw new SecretStoreException($"金鑰「{name}」無法解密，請重新輸入。", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default)
    {
        var name = key.RequireName();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(ct).ConfigureAwait(false);
            var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(value.Reveal()));
            file.Items[name] = Convert.ToBase64String(protectedBytes);
            await WriteFileAsync(file, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        var name = key.RequireName();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(ct).ConfigureAwait(false);
            if (!file.Items.Remove(name))
                return false;
            await WriteFileAsync(file, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default)
    {
        var name = key.RequireName();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await ReadFileAsync(ct).ConfigureAwait(false);
            return file.Items.ContainsKey(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SecretsFile> ReadFileAsync(CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new SecretsFile();
        }
        catch (DirectoryNotFoundException)
        {
            return new SecretsFile();
        }
        catch (IOException ex)
        {
            throw new SecretStoreException("無法讀取金鑰檔，請重新輸入金鑰。", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SecretStoreException("沒有權限讀取金鑰檔，請重新輸入金鑰。", ex);
        }

        try
        {
            var file = JsonSerializer.Deserialize<SecretsFile>(bytes, JsonOptions)
                       ?? throw new JsonException("內容為 null");
            if (file.Version != CurrentVersion)
                throw new SecretStoreException($"金鑰檔版本 {file.Version} 不支援，請重新輸入金鑰。");
            return new SecretsFile { Version = file.Version, Items = new Dictionary<string, string>(file.Items ?? [], StringComparer.Ordinal) };
        }
        catch (JsonException ex)
        {
            // 不附上檔案內容
            throw new SecretStoreException("金鑰檔已損毀，請重新輸入金鑰。", ex);
        }
    }

    private async Task WriteFileAsync(SecretsFile file, CancellationToken ct)
    {
        try
        {
            await AtomicFile.WriteAllBytesAsync(_path, JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException("無法寫入金鑰檔。", ex);
        }
    }

    private sealed class SecretsFile
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("items")]
        public Dictionary<string, string> Items { get; set; } = new(StringComparer.Ordinal);
    }
}
