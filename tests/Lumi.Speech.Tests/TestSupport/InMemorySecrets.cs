using Lumi.Core.Secrets;

namespace Lumi.Speech.Tests.TestSupport;

/// <summary>記憶體版的 ISecretStore；<see cref="ThrowOnGet"/> 模擬鑰匙圈讀取失敗。</summary>
internal sealed class InMemorySecrets : ISecretStore
{
    public Dictionary<string, string> Items { get; } = [];

    public SecretStoreException? ThrowOnGet { get; set; }

    public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        if (ThrowOnGet is { } ex)
            throw ex;
        return Task.FromResult(Items.TryGetValue(key.Name, out var v) ? new SecretValue(v) : null);
    }

    public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default)
    {
        Items[key.Name] = value.Reveal();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(Items.Remove(key.Name));

    public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(Items.ContainsKey(key.Name));
}
