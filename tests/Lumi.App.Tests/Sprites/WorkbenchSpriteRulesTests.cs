using Lumi.App.Sprites;

namespace Lumi.App.Tests.Sprites;

/// <summary>工作台角色的版位與尺寸規則（plan/27 第 8.3 節）：純函式，逐列測。</summary>
public class WorkbenchSpriteRulesTests
{
    private static WorkbenchSpritePlacement Place(double modeButtonLeft, double boxWidth, double columnLeft, double columnRight) =>
        WorkbenchSpriteRules.PlaceOnToolbar(modeButtonLeft, boxWidth, columnLeft, columnRight);

    // ---- ResolveSlot ----

    [Theory]
    // enabled, showWelcome, loading, viewingRun, toolbarFits, previous, expected
    [InlineData(false, true, false, false, true, WorkbenchSpriteSlot.Toolbar, WorkbenchSpriteSlot.None)]
    [InlineData(false, false, false, false, false, WorkbenchSpriteSlot.None, WorkbenchSpriteSlot.None)]
    [InlineData(true, false, false, true, true, WorkbenchSpriteSlot.Toolbar, WorkbenchSpriteSlot.None)]
    [InlineData(true, true, false, true, false, WorkbenchSpriteSlot.Welcome, WorkbenchSpriteSlot.None)]
    [InlineData(true, false, true, false, false, WorkbenchSpriteSlot.Toolbar, WorkbenchSpriteSlot.Toolbar)]
    [InlineData(true, true, true, false, false, WorkbenchSpriteSlot.Welcome, WorkbenchSpriteSlot.Welcome)]
    [InlineData(true, true, true, false, true, WorkbenchSpriteSlot.None, WorkbenchSpriteSlot.None)]
    [InlineData(true, false, true, true, true, WorkbenchSpriteSlot.Toolbar, WorkbenchSpriteSlot.None)]
    [InlineData(true, true, false, false, true, WorkbenchSpriteSlot.None, WorkbenchSpriteSlot.Welcome)]
    [InlineData(true, false, false, false, true, WorkbenchSpriteSlot.None, WorkbenchSpriteSlot.Toolbar)]
    [InlineData(true, false, false, false, false, WorkbenchSpriteSlot.Toolbar, WorkbenchSpriteSlot.None)]
    public void ResolveSlot_FollowsTheTable(bool enabled, bool showWelcome, bool loading, bool viewingRun, bool toolbarFits,
        WorkbenchSpriteSlot previous, WorkbenchSpriteSlot expected)
    {
        var actual = WorkbenchSpriteRules.ResolveSlot(enabled, showWelcome, loading, viewingRun, toolbarFits, previous);

        Assert.Equal(expected, actual);
    }

    // ---- PlaceOnToolbar ----

    [Fact]
    public void Windows1040_Fits_WithReserve()
    {
        var placement = Place(683, 56, 238, 944);

        Assert.True(placement.Fits);
        Assert.Equal(619, placement.BoxLeft);
        Assert.Equal(333, placement.CapsuleReserve);
    }

    [Fact]
    public void Windows700_DoesNotFit_AndReservesNothing()
    {
        var placement = Place(371, 56, 96, 604);

        Assert.False(placement.Fits);
        Assert.Equal(0, placement.CapsuleReserve);
    }

    [Fact]
    public void Mac600_Fits()
    {
        var placement = Place(468, 56, 96, 504);

        Assert.True(placement.Fits);
        Assert.Equal(404, placement.BoxLeft);
    }

    [Fact]
    public void ExactlyMinCapsuleWidth_Fits()
    {
        // boxLeft − 8 − 96 = 280
        var placement = Place(448, 56, 96, 500);

        Assert.True(placement.Fits);
        Assert.Equal(384, placement.BoxLeft);
    }

    [Fact]
    public void HalfPixelShort_DoesNotFit()
    {
        var placement = Place(447.5, 56, 96, 500);

        Assert.False(placement.Fits);
        Assert.Equal(0, placement.CapsuleReserve);
    }

    [Fact]
    public void BoxRightPastTheColumnRight_DoesNotFit()
    {
        // boxRight = 440，欄右緣 439
        var placement = Place(448, 56, 96, 439);

        Assert.False(placement.Fits);
        Assert.Equal(0, placement.CapsuleReserve);
    }

    [Fact]
    public void BoxRightOnTheColumnRight_Fits()
    {
        var placement = Place(448, 56, 96, 440);

        Assert.True(placement.Fits);
    }

    [Fact]
    public void Reserve_IsColumnRightMinusBoxLeftMinusGap()
    {
        // 500 − (384 − 8) = 124
        var placement = Place(448, 56, 96, 500);

        Assert.Equal(500 - (placement.BoxLeft - WorkbenchSpriteRules.Gap), placement.CapsuleReserve);
        Assert.Equal(124, placement.CapsuleReserve);
    }

    // ---- PeekDepth ----

    [Fact]
    public void PeekDepth_Corgi_IsFromTheVisibleTop()
    {
        // 柯基：框 56，頭頂 6.72 → 露出 16 → 下沉 25.28
        Assert.Equal(25.28, WorkbenchSpriteRules.PeekDepth(56, 6.72), 6);
    }

    [Fact]
    public void PeekDepth_Robot_IsFromTheVisibleTop()
    {
        // 機器人：框 48，頭頂 7.68 → 下沉 16.32
        Assert.Equal(16.32, WorkbenchSpriteRules.PeekDepth(48, 7.68), 6);
    }

    [Fact]
    public void PeekDepth_Orb_IsFromTheVisibleTop()
    {
        // 光球：框 86.4，頭頂 23.04 → 下沉 39.36
        Assert.Equal(39.36, WorkbenchSpriteRules.PeekDepth(86.4, 23.04), 6);
    }

    [Fact]
    public void PeekDepth_IsNeverNegative()
    {
        Assert.Equal(0, WorkbenchSpriteRules.PeekDepth(10, 50));
    }
}
