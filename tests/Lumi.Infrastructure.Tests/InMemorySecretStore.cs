using Lumi.Core.Secrets;

namespace Lumi.Infrastructure.Tests;

internal sealed class InMemorySecretStore : ISecretStore
{
    public Dictionary<string, string> Items { get; } = [];

    public Task<SecretValue?> GetAsync(SecretKey key, CancellationToken ct = default) =>
        Task.FromResult(Items.TryGetValue(key.Name, out var v) ? new SecretValue(v) : null);

    public Task SetAsync(SecretKey key, SecretValue value, CancellationToken ct = default)
    {
        Items[key.Name] = value.Reveal();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(Items.Remove(key.Name));

    public Task<bool> ExistsAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult(Items.ContainsKey(key.Name));
}
