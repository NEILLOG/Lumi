using System.Collections.Concurrent;
using Lumi.Core.Secrets;

namespace Lumi.Platform.Tests;

/// <summary>測試用的記憶體實作（其他專案的測試也可以拿來假裝有金鑰）。</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, SecretValue> _items = new(StringComparer.Ordinal);

    public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) =>
        Task.FromResult(_items.GetValueOrDefault(key.RequireName()));

    public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default)
    {
        _items[key.RequireName()] = value;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) =>
        Task.FromResult(_items.TryRemove(key.RequireName(), out _));

    public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) =>
        Task.FromResult(_items.ContainsKey(key.RequireName()));
}
