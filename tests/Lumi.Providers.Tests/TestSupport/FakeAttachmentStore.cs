using Lumi.Core.Context;

namespace Lumi.Providers.Tests.TestSupport;

internal sealed class FakeAttachmentStore : IAttachmentStore
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
    {
        var name = $"f{Files.Count + 1}.{ImageFormats.Extension(mediaType)}";
        Files[name] = data.ToArray();
        return Task.FromResult(name);
    }

    public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) => Task.FromResult(Files.GetValueOrDefault(fileName));

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
}
