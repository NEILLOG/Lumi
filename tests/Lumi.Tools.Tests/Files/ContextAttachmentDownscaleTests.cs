using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Usage;
using Xunit;

namespace Lumi.Tools.Tests.Files;

/// <summary>L5：加入上下文的圖片在存成附件前經過 <see cref="IImageDownscaler"/>；只在存附件時縮，預估 token 不縮。</summary>
public sealed class ContextAttachmentDownscaleTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FileToolOptions _options = new();
    private readonly RecordingStore _attachments = new();

    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    // 縮過的假資料：這裡只看位元組，不解碼
    private static readonly byte[] SmallerJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9, 9];

    public void Dispose() => _tree.Dispose();

    private sealed class RecordingStore : IAttachmentStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];

        public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
        {
            var name = $"file{Files.Count + 1}.{ImageFormats.Extension(mediaType)}";
            Files[name] = data.ToArray();
            return Task.FromResult(name);
        }

        public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) =>
            Task.FromResult(Files.GetValueOrDefault(fileName));

        public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeDownscaler(Func<byte[], string, int, DownscaledImage?> behavior) : IImageDownscaler
    {
        public List<(byte[] Data, string MediaType, int MaxEdge)> Calls { get; } = [];

        public DownscaledImage? Downscale(byte[] data, string mediaType, int maxEdge)
        {
            Calls.Add((data, mediaType, maxEdge));
            return behavior(data, mediaType, maxEdge);
        }
    }

    private ContextAttachmentBuilder Builder(IImageDownscaler? downscaler) =>
        new(_options, _attachments, new HeuristicTokenEstimator(), downscaler);

    private static ContextItem Item(string path) =>
        new(ContextItem.NewId(), ContextKind.Image, path, Path.GetFileName(path), DateTimeOffset.UtcNow, null);

    private string WritePng(string name)
    {
        var path = _tree.Path_(name);
        File.WriteAllBytes(path, PngBytes);
        return path;
    }

    [Fact]
    public async Task WithDownscaler_TheDownscaledBytesAndMediaType_AreStored()
    {
        var file = WritePng("big.png");
        var fake = new FakeDownscaler((_, _, _) => new DownscaledImage(SmallerJpeg, "image/jpeg"));

        var block = Assert.IsType<ImageBlock>(Assert.Single((await Builder(fake).BuildAsync([Item(file)])).Blocks));

        Assert.Equal("image/jpeg", block.MediaType);
        Assert.EndsWith(".jpg", block.FileName);
        Assert.Equal(SmallerJpeg, _attachments.Files[block.FileName]);
    }

    [Fact]
    public async Task DownscalerReturnsNull_TheOriginalBytesAreStored()
    {
        var file = WritePng("small.png");
        var fake = new FakeDownscaler((_, _, _) => null);

        var block = Assert.IsType<ImageBlock>(Assert.Single((await Builder(fake).BuildAsync([Item(file)])).Blocks));

        Assert.Equal("image/png", block.MediaType);
        Assert.Equal(PngBytes, _attachments.Files[block.FileName]);
    }

    [Fact]
    public async Task WithoutDownscaler_TheOriginalBytesAreStored()
    {
        var file = WritePng("a.png");

        var block = Assert.IsType<ImageBlock>(Assert.Single((await Builder(null).BuildAsync([Item(file)])).Blocks));

        Assert.Equal(PngBytes, _attachments.Files[block.FileName]);
    }

    [Fact]
    public async Task WhenSaving_DownscalerIsCalledWithTheOriginalBytes_AndMaxEdge1568()
    {
        var file = WritePng("a.png");
        var fake = new FakeDownscaler((_, _, _) => null);

        await Builder(fake).BuildAsync([Item(file)]);

        var call = Assert.Single(fake.Calls);
        Assert.Equal(PngBytes, call.Data);
        Assert.Equal("image/png", call.MediaType);
        Assert.Equal(1568, call.MaxEdge);
        Assert.Equal(1568, ContextAttachmentBuilder.MaxImageEdge);
    }

    [Fact]
    public async Task EstimateTokens_DoesNotDownscale()
    {
        var file = WritePng("a.png");
        var fake = new FakeDownscaler((_, _, _) => new DownscaledImage(SmallerJpeg, "image/jpeg"));

        var tokens = await Builder(fake).EstimateTokensAsync([Item(file)]);

        Assert.Empty(fake.Calls);
        Assert.Empty(_attachments.Files);
        Assert.True(tokens > 0);
    }
}
