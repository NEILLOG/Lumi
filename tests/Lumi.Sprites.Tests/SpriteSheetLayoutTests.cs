using Avalonia;
using Lumi.Sprites.Assets;

namespace Lumi.Sprites.Tests;

public class SpriteSheetLayoutTests
{
    // ---- 切格 ----

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 280, 0)]
    [InlineData(3, 840, 0)]
    [InlineData(4, 0, 280)]      // 4 欄：第 5 格換到第二列
    [InlineData(7, 840, 280)]
    [InlineData(11, 840, 560)]
    public void SourceRect_GoesLeftToRight_TopToBottom(int index, double x, double y)
    {
        var rect = SpriteSheetLayout.SourceRect(new Size(280, 280), 4, index);

        Assert.Equal(new Rect(x, y, 280, 280), rect);
    }

    [Fact]
    public void SourceRect_WorksForNonSquareCells()
    {
        Assert.Equal(new Rect(60, 45, 30, 45), SpriteSheetLayout.SourceRect(new Size(30, 45), 3, 5));   // 3 欄：第 6 格在第二列第三欄
    }

    // ---- 一般縮放：等比例、底部置中 ----

    [Fact]
    public void ASquareCell_FillsASquareOuter()
    {
        Assert.Equal(new Rect(0, 0, 112, 112), SpriteSheetLayout.Destination(new Size(112, 112), new Size(280, 280), null, 2));
    }

    [Fact]
    public void ACellWiderThanTheOuter_IsScaledToFitTheWidth_AndSitsOnTheBottom()
    {
        var destination = SpriteSheetLayout.Destination(new Size(100, 100), new Size(200, 100), null, 1);

        Assert.Equal(new Rect(0, 50, 100, 50), destination);   // 寬 100、高 50，貼著底部
    }

    [Fact]
    public void ACellTallerThanTheOuter_IsScaledToFitTheHeight_AndCenteredHorizontally()
    {
        var destination = SpriteSheetLayout.Destination(new Size(100, 100), new Size(100, 200), null, 1);

        Assert.Equal(new Rect(25, 0, 50, 100), destination);
    }

    [Fact]
    public void TheDestination_NeverExceedsTheOuter()
    {
        foreach (var (w, h) in new[] { (10.0, 10.0), (300, 20), (20, 300), (280, 280) })
        {
            var destination = SpriteSheetLayout.Destination(new Size(90, 130), new Size(w, h), null, 1);

            Assert.True(destination.X >= -1e-9 && destination.Y >= -1e-9);
            Assert.True(destination.Right <= 90 + 1e-9 && destination.Bottom <= 130 + 1e-9);
            Assert.Equal(130, destination.Bottom, 6);   // 一律貼底
        }
    }

    [Theory]
    [InlineData(0, 10, 100, 100)]
    [InlineData(10, 0, 100, 100)]
    [InlineData(10, 10, 0, 100)]
    [InlineData(10, 10, 100, -1)]
    public void ADegenerateSize_GivesAnEmptyRect(double cellW, double cellH, double outerW, double outerH)
    {
        Assert.Equal(default, SpriteSheetLayout.Destination(new Size(outerW, outerH), new Size(cellW, cellH), null, 1));
    }

    // ---- 整數倍貼齊 ----

    [Theory]
    [InlineData(112.0, 1.0, 112.0)]     // 中檔、1 倍：4 DIP 一個邏輯像素，剛好填滿
    [InlineData(112.0, 2.0, 112.0)]     // 中檔、2 倍：8 裝置像素一個，剛好填滿
    [InlineData(89.6, 2.0, 84.0)]       // 小檔、2 倍：179.2 px → 6 px 一個 → 168 px
    [InlineData(145.6, 2.0, 140.0)]     // 大檔、2 倍：291.2 px → 10 px 一個 → 280 px
    [InlineData(89.6, 1.0, 84.0)]       // 小檔、1 倍：89.6 px → 3 px 一個 → 84 px
    [InlineData(145.6, 1.0, 140.0)]     // 大檔、1 倍：145.6 px → 5 px 一個 → 140 px
    [InlineData(112.0, 1.25, 112.0)]    // Windows 125%：5 裝置像素一個
    [InlineData(112.0, 1.5, 112.0)]     // Windows 150%：6 裝置像素一個
    public void WithAPixelGrid_EachLogicalPixelIsAWholeNumberOfDevicePixels(double outer, double scaling, double expectedDip)
    {
        var destination = SpriteSheetLayout.Destination(new Size(outer, outer), new Size(280, 280), 28, scaling);

        Assert.Equal(expectedDip, destination.Width, 6);
        Assert.Equal(destination.Width, destination.Height, 6);
        var devicePixels = destination.Width * scaling;
        Assert.Equal(0, devicePixels % 28, 6);   // 整張圖是 28 的整數倍：每個邏輯像素一樣粗
    }

    [Fact]
    public void WithAPixelGrid_TheImageStaysInsideTheOuter_CenteredOnTheBottom()
    {
        var destination = SpriteSheetLayout.Destination(new Size(145.6, 145.6), new Size(280, 280), 28, 2);

        Assert.True(destination.Width <= 145.6);
        Assert.Equal((145.6 - destination.Width) / 2, destination.X, 0);   // 水平置中（對齊到整個裝置像素，所以容許不到一個裝置像素的誤差）
        Assert.Equal(145.6, destination.Bottom, 0);                         // 腳底的基準線貼著底部
        Assert.True(destination.X >= 0);
    }

    [Fact]
    public void WithAPixelGrid_ThePositionIsAlignedToTheDevicePixelGrid()
    {
        var destination = SpriteSheetLayout.Destination(new Size(145.6, 145.6), new Size(280, 280), 28, 2);

        Assert.Equal(0, destination.X * 2 % 1, 6);
        Assert.Equal(0, destination.Y * 2 % 1, 6);
    }

    [Fact]
    public void AnOuterSmallerThanTheGrid_StillDrawsAtLeastOneDevicePixelPerLogicalPixel()
    {
        var destination = SpriteSheetLayout.Destination(new Size(10, 10), new Size(280, 280), 28, 1);

        Assert.Equal(28, destination.Width, 6);   // 超出 outer（只在極端的小尺寸）；至少每個邏輯像素 1 個裝置像素
    }

    [Fact]
    public void ANonSquareCell_KeepsSquareLogicalPixels()
    {
        // 格子 200×300、寬 20 個邏輯像素：邏輯像素 10 px，高度 30 個
        var destination = SpriteSheetLayout.Destination(new Size(120, 180), new Size(200, 300), 20, 1);

        Assert.Equal(6, destination.Width / 20, 6);    // 每個邏輯像素 6 裝置像素
        Assert.Equal(180, destination.Height, 6);      // 30 × 6
    }

    [Fact]
    public void ARenderScalingThatIsZero_IsTreatedAsOne()
    {
        Assert.Equal(
            SpriteSheetLayout.Destination(new Size(112, 112), new Size(280, 280), 28, 1),
            SpriteSheetLayout.Destination(new Size(112, 112), new Size(280, 280), 28, 0));
    }

    [Fact]
    public void WithoutAPixelGrid_TheScalingDoesNotChangeTheRect()
    {
        Assert.Equal(
            SpriteSheetLayout.Destination(new Size(89.6, 89.6), new Size(280, 280), null, 1),
            SpriteSheetLayout.Destination(new Size(89.6, 89.6), new Size(280, 280), null, 2));
    }
}
