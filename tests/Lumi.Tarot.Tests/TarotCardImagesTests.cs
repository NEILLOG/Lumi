namespace Lumi.Tarot.Tests;

public sealed class TarotCardImagesTests
{
    private const string ValidFrameJson =
        """{"size":{"width":1400,"height":2400},"window":{"x":112,"y":265,"width":1176,"height":1815}}""";

    [Fact]
    public void OpenCard_EmbeddedImageIsReadable()
    {
        using var stream = TarotCardImages.Default.OpenCard("major_00");

        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        Assert.True(buffer.Length > 0);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("MAJOR_00")]
    [InlineData("")]
    public void OpenCard_BadIdReturnsNull(string id)
    {
        Assert.Null(TarotCardImages.Default.OpenCard(id));
    }

    [Fact]
    public void OpenFrame_IsNotNull()
    {
        using var stream = TarotCardImages.Default.OpenFrame();

        Assert.NotNull(stream);
    }

    [Fact]
    public void FrameWindow_IsLoadedFromEmbeddedFrameInfo()
    {
        var window = TarotCardImages.Default.FrameWindow;

        Assert.NotNull(window);
        Assert.InRange(window.CenterY, 0.4, 0.6);
    }

    [Fact]
    public void ParseFrameInfo_ValidContentIsConvertedToRatios()
    {
        var window = TarotCardImages.ParseFrameInfo(TarotTestSupport.Text(ValidFrameJson));

        Assert.NotNull(window);
        Assert.InRange(window.Left, 0.08 - 0.001, 0.08 + 0.001);
        Assert.InRange(window.Top, 0.1104 - 0.001, 0.1104 + 0.001);
    }

    [Fact]
    public void ParseFrameInfo_NullStreamIsNull()
    {
        Assert.Null(TarotCardImages.ParseFrameInfo(null));
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("""{"window":{"x":1,"y":1,"width":10,"height":10}}""")]
    [InlineData("""{"size":{"width":1400,"height":2400},"window":{"x":112,"y":265,"width":0,"height":1815}}""")]
    [InlineData("""{"size":{"width":1400,"height":2400},"window":{"x":1300,"y":265,"width":200,"height":1815}}""")]
    [InlineData("""{"size":{"width":1400,"height":2400},"window":{"x":112,"y":"265","width":1176,"height":1815}}""")]
    [InlineData("")]
    public void ParseFrameInfo_InvalidContentIsNullAndDoesNotThrow(string json)
    {
        Assert.Null(TarotCardImages.ParseFrameInfo(TarotTestSupport.Text(json)));
    }
}
