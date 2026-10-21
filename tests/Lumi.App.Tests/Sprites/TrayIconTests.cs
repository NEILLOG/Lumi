using Avalonia.Headless.XUnit;
using Lumi.App.Sprites;

namespace Lumi.App.Tests.Sprites;

public class TrayIconTests
{
    [AvaloniaFact]
    public void TheOrbIcon_CanBeDrawn_AtSeveralSizes()
    {
        foreach (var size in new[] { 16, 32, 64 })
            Assert.NotNull(TrayIconFactory.CreateOrbIcon(size));
    }
}
