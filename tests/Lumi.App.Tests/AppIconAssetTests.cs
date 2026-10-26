using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using SkiaSharp;

namespace Lumi.App.Tests;

/// <summary>M20 20E：tools/Lumi.IconGen 產出的圖示檔（src/Lumi.App/Assets/Icons）格式與內容檢查。</summary>
public class AppIconAssetTests
{
    [AvaloniaFact]
    public void Lumi1024_IsSquare_TransparentCorner_AndBrightCenter()
    {
        using var bitmap = DecodeAsset("lumi-1024.png");

        Assert.Equal(1024, bitmap.Width);
        Assert.Equal(1024, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.True(bitmap.GetPixel(512, 512).Red > 200);
    }

    [AvaloniaFact]
    public void Lumi256_IsSquare_AndTransparentCorner()
    {
        using var bitmap = DecodeAsset("lumi-256.png");

        Assert.Equal(256, bitmap.Width);
        Assert.Equal(256, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
    }

    [AvaloniaFact]
    public void LumiTray64_IsSquare_AndTransparentCorner()
    {
        using var bitmap = DecodeAsset("lumi-tray-64.png");

        Assert.Equal(64, bitmap.Width);
        Assert.Equal(64, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
    }

    [Fact]
    public void LumiIco_HasSevenSizes_InOrder()
    {
        var bytes = File.ReadAllBytes(IconPath("Lumi.ico"));

        // ICONDIR：reserved 0、type 1（圖示）、count 7
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x07, 0x00 }, bytes.AsSpan(0, 6).ToArray());

        // 七個 ICONDIRENTRY 的寬度；256 寫 0
        int[] expectedWidths = [16, 24, 32, 48, 64, 128, 0];
        for (var i = 0; i < expectedWidths.Length; i++)
            Assert.Equal(expectedWidths[i], bytes[6 + 16 * i]);
    }

    private static SKBitmap DecodeAsset(string fileName)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://Lumi.App/Assets/Icons/{fileName}"));
        return SKBitmap.Decode(stream) ?? throw new InvalidOperationException($"無法解碼 {fileName}");
    }

    private static string IconPath(string fileName, [CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "src", "Lumi.App", "Assets", "Icons", fileName));
}
