using Lumi.Core.Platform;

namespace Lumi.Core.Tests;

public class NullOverlayTests
{
    [Fact]
    public void NullOverlay_IsTheUnsupportedFallback_AndDoesNothing()
    {
        IOverlayPlatform overlay = new NullOverlay();

        overlay.Attach(12345);
        overlay.SetClickThrough(true);

        Assert.False(overlay.IsSupported);
        Assert.False(overlay.ClickThroughActive);
        Assert.False(overlay.TryGetCursorInWindow(2, 300, out var point));
        Assert.Equal(default, point);
        Assert.False(string.IsNullOrWhiteSpace(overlay.Name));
    }
}
