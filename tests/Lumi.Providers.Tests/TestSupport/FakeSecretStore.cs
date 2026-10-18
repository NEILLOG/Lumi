using Lumi.Core.Secrets;

namespace Lumi.Providers.Tests.TestSupport;

/// <summary>測試用：不實際儲存，只回答固定的答案。1B 的契約測試不涵蓋這裡，所以另外寫一個最小版本。</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly SecretValue? _value;
    private readonly SecretStoreException? _throwOnGet;

    private FakeSecretStore(SecretValue? value, SecretStoreException? throwOnGet)
    {
        _value = value;
        _throwOnGet = throwOnGet;
    }

    public static FakeSecretStore WithKey(string apiKey) => new(new SecretValue(apiKey), null);
    public static FakeSecretStore Empty() => new(null, null);
    public static FakeSecretStore Throwing(SecretStoreException ex) => new(null, ex);

    public List<string> RequestedKeys { get; } = [];

    public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        RequestedKeys.Add(key.Name);
        return _throwOnGet is not null ? throw _throwOnGet : Task.FromResult(_value);
    }

    public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default) =>
        throw new NotSupportedException("測試不需要寫入");

    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) =>
        throw new NotSupportedException("測試不需要刪除");

    public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) =>
        Task.FromResult(_value is not null);
}
