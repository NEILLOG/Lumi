using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Lumi.Core.Secrets;
using Lumi.Platform.Interop.Mac;
using Microsoft.Extensions.Options;

namespace Lumi.Platform.Secrets;

/// <summary>
/// macOS：登入鑰匙圈的 generic password 項目。
/// service = <see cref="SecretStoreOptions.KeychainService"/>，account = <see cref="SecretKey.Name"/>。
/// </summary>
/// <remarks>
/// 使用登入鑰匙圈（legacy file-based keychain），不使用 data protection keychain：
/// 後者需要簽章與 keychain-access-groups entitlement，未簽章的開發版會直接失敗（-34018）。M11 簽章後再評估。
/// 開發期的麻煩：存取權限綁在程式簽章上，<c>dotnet run</c> 每次重新建置簽章都會變，系統會再問一次授權。
/// Keychain 呼叫可能卡在系統授權視窗，所以一律丟到背景執行緒。
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore : ISecretStore
{
    private readonly string _service;

    public MacKeychainSecretStore(IOptions<SecretStoreOptions> options)
        : this(options.Value.KeychainService)
    {
    }

    public MacKeychainSecretStore(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        _service = service;
    }

    public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        var name = key.RequireName();
        return Task.Run(() => Get(name), ct);
    }

    public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default)
    {
        var name = key.RequireName();
        return Task.Run(() => Set(name, value), ct);
    }

    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        var name = key.RequireName();
        return Task.Run(() => Delete(name), ct);
    }

    public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default)
    {
        var name = key.RequireName();
        return Task.Run(() => Exists(name), ct);
    }

    private SecretValue? Get(string name)
    {
        using var query = NewQuery(name);
        CoreFoundation.Set(query, Security.KSecReturnData, CoreFoundation.BooleanTrue);
        CoreFoundation.Set(query, Security.KSecMatchLimit, Security.KSecMatchLimitOne);

        var status = Security.SecItemCopyMatching(query, out var raw);
        if (status == Security.ErrSecItemNotFound)
            return null;
        ThrowIfFailed(status, "讀取", name);

        using var data = new SafeCFHandle(raw);
        var length = (int)CoreFoundation.CFDataGetLength(data);
        var pointer = CoreFoundation.CFDataGetBytePtr(data);
        var text = length == 0 ? "" : Marshal.PtrToStringUTF8(pointer, length);
        GC.KeepAlive(data);

        if (string.IsNullOrEmpty(text))
            throw new SecretStoreException($"鑰匙圈中的金鑰「{name}」內容是空的，請重新輸入。");
        return new SecretValue(text);
    }

    private void Set(string name, SecretValue value)
    {
        using var data = CoreFoundation.CreateData(Encoding.UTF8.GetBytes(value.Reveal()));
        using var label = CoreFoundation.CreateString($"Lumi：{name}");

        // 先更新；項目不存在才新增
        using (var query = NewQuery(name))
        using (var changes = CoreFoundation.CreateDictionary())
        {
            CoreFoundation.Set(changes, Security.KSecValueData, data);
            CoreFoundation.Set(changes, Security.KSecAttrLabel, label);

            var updateStatus = Security.SecItemUpdate(query, changes);
            if (updateStatus != Security.ErrSecItemNotFound)
            {
                ThrowIfFailed(updateStatus, "寫入", name);
                return;
            }
        }

        using var add = NewQuery(name);
        CoreFoundation.Set(add, Security.KSecValueData, data);
        CoreFoundation.Set(add, Security.KSecAttrLabel, label);
        ThrowIfFailed(Security.SecItemAdd(add, IntPtr.Zero), "寫入", name);
    }

    private bool Delete(string name)
    {
        using var query = NewQuery(name);
        var status = Security.SecItemDelete(query);
        if (status == Security.ErrSecItemNotFound)
            return false;
        ThrowIfFailed(status, "刪除", name);
        return true;
    }

    private bool Exists(string name)
    {
        // 只要求屬性，不要求資料：確認存在不需要授權讀取內容
        using var query = NewQuery(name);
        CoreFoundation.Set(query, Security.KSecReturnAttributes, CoreFoundation.BooleanTrue);
        CoreFoundation.Set(query, Security.KSecMatchLimit, Security.KSecMatchLimitOne);

        var status = Security.SecItemCopyMatching(query, out var raw);
        if (status == Security.ErrSecItemNotFound)
            return false;
        ThrowIfFailed(status, "查詢", name);

        using var attributes = new SafeCFHandle(raw);
        return true;
    }

    /// <summary>generic password 的查詢字典：class + service + account。</summary>
    private SafeCFHandle NewQuery(string name)
    {
        var query = CoreFoundation.CreateDictionary();
        using var service = CoreFoundation.CreateString(_service);
        using var account = CoreFoundation.CreateString(name);

        CoreFoundation.Set(query, Security.KSecClass, Security.KSecClassGenericPassword);
        CoreFoundation.Set(query, Security.KSecAttrService, service);
        CoreFoundation.Set(query, Security.KSecAttrAccount, account);
        return query;
    }

    // 訊息只含金鑰「名稱」與 OSStatus 數字，不含內容
    private static void ThrowIfFailed(int status, string action, string name)
    {
        if (status == Security.ErrSecSuccess)
            return;

        var reason = status switch
        {
            Security.ErrSecUserCanceled => "使用者取消了授權",
            Security.ErrSecAuthFailed => "鑰匙圈授權失敗",
            Security.ErrSecInteractionNotAllowed => "鑰匙圈被鎖定，且目前無法跳出授權視窗",
            _ => "鑰匙圈回傳錯誤",
        };
        throw new SecretStoreException($"{action}金鑰「{name}」失敗：{reason}（OSStatus {status}）。請重新輸入金鑰。");
    }
}
