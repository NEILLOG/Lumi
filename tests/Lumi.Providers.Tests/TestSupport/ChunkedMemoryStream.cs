namespace Lumi.Providers.Tests.TestSupport;

/// <summary>
/// 故意把每次讀取限制在很小的位元組數，並在每次讀取前讓出控制權，模擬真實網路的小封包到達與可被取消。
/// 用來驗證：中文、組合 emoji 不會因為剛好被切在 UTF-8 多位元組中間而損壞；取消可以在任何一次讀取之間生效。
/// </summary>
internal sealed class ChunkedMemoryStream(byte[] data, int chunkSize) : MemoryStream(data)
{
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return await base.ReadAsync(buffer, offset, Math.Min(count, chunkSize), cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
