using System.ComponentModel;
using System.Diagnostics;
using SkiaSharp;

// Lumi 光球圖示產生器（M20 包 20E）。以 SkiaSharp 繪製，全部以 1024 為基準座標（方塊版）或直接指定的像素座標。
// 用法：dotnet run --project tools/Lumi.IconGen -- <輸出資料夾>
// 產出：lumi-1024.png（方塊版）、lumi-256.png（視窗圖示）、lumi-tray-64.png（托盤）、Lumi.ico（Windows）、Lumi.icns（僅 macOS）。

if (args.Length < 1)
{
    Console.Error.WriteLine("用法：dotnet run --project tools/Lumi.IconGen -- <輸出資料夾>");
    return 2;
}

var outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);

// 光球配色（與 TrayIconFactory 原本的配色一致）
static SKColor Rgb(uint rgb, byte alpha = 0xFF) =>
    new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);

// 光球：外暈（可選）＋球體＋高光
static void DrawOrb(SKCanvas canvas, float cx, float cy, float r, bool withGlow)
{
    if (withGlow)
    {
        using var glowShader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy),
            r * 1.35f,
            [Rgb(0xF5B76A, 0x90), Rgb(0xF5B76A, 0x00)],
            [0.55f, 1f],
            SKShaderTileMode.Clamp);
        using var glowPaint = new SKPaint { Shader = glowShader, IsAntialias = true };
        canvas.DrawCircle(cx, cy, r * 1.35f, glowPaint);
    }

    using (var bodyShader = SKShader.CreateRadialGradient(
               new SKPoint(cx - 0.15f * r, cy - 0.2f * r),
               1.15f * r,
               [Rgb(0xFFFFFF), Rgb(0xFFE9C7), Rgb(0xF5B76A), Rgb(0xC9783A)],
               [0f, 0.3f, 0.7f, 1f],
               SKShaderTileMode.Clamp))
    using (var bodyPaint = new SKPaint { Shader = bodyShader, IsAntialias = true })
    {
        canvas.DrawCircle(cx, cy, r, bodyPaint);
    }

    using var highlightShader = SKShader.CreateRadialGradient(
        new SKPoint(cx - 0.3f * r, cy - 0.38f * r),
        0.22f * r,
        [Rgb(0xFFFFFF, 0xC0), Rgb(0xFFFFFF, 0x00)],
        null,
        SKShaderTileMode.Clamp);
    using var highlightPaint = new SKPaint { Shader = highlightShader, IsAntialias = true };
    canvas.DrawCircle(cx - 0.3f * r, cy - 0.38f * r, 0.22f * r, highlightPaint);
}

// 方塊版：1024 基準座標，呼叫端可先 Scale 成其他尺寸（用於 .icns 各尺寸重畫）
static void DrawBadge(SKCanvas canvas)
{
    var rrect = new SKRoundRect(new SKRect(100, 100, 924, 924), 185, 185);
    using (var bgShader = SKShader.CreateLinearGradient(
               new SKPoint(0, 100),
               new SKPoint(0, 924),
               [Rgb(0x34463F), Rgb(0x1C2622)],
               null,
               SKShaderTileMode.Clamp))
    using (var bgPaint = new SKPaint { Shader = bgShader, IsAntialias = true })
    {
        canvas.DrawRoundRect(rrect, bgPaint);
    }

    canvas.ClipRoundRect(rrect, antialias: true);
    DrawOrb(canvas, 512, 512, 250, withGlow: true);
}

static byte[] RenderPng(int size, Action<SKCanvas> draw)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    draw(canvas);
    canvas.Flush();
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// 1. lumi-1024.png：方塊版
var png1024 = RenderPng(1024, DrawBadge);
File.WriteAllBytes(Path.Combine(outDir, "lumi-1024.png"), png1024);

// 2. lumi-256.png：視窗圖示，只有光球
var png256 = RenderPng(256, c => DrawOrb(c, 128, 128, 88, withGlow: true));
File.WriteAllBytes(Path.Combine(outDir, "lumi-256.png"), png256);

// 3. lumi-tray-64.png：托盤，不畫外暈
var pngTray = RenderPng(64, c => DrawOrb(c, 32, 32, 27, withGlow: false));
File.WriteAllBytes(Path.Combine(outDir, "lumi-tray-64.png"), pngTray);

// 4. Lumi.ico：七個尺寸的光球（無方塊），PNG 壓縮
int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
var icoImages = icoSizes
    .Select(size => (Size: size, Png: RenderPng(size, c =>
        DrawOrb(c, size / 2f, size / 2f, 0.34f * size, withGlow: size > 32))))
    .ToArray();
WriteIco(Path.Combine(outDir, "Lumi.ico"), icoImages);

// 5. Lumi.icns：僅 macOS（需要 iconutil）
var icnsPath = Path.Combine(outDir, "Lumi.icns");
if (!TryBuildIcns(icnsPath))
{
    Console.WriteLine("找不到 iconutil（非 macOS），略過 Lumi.icns；請在 macOS 上重新執行以產生該檔。");
}

Console.WriteLine($"完成：{outDir}");
return 0;

static void WriteIco(string path, (int Size, byte[] Png)[] images)
{
    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);

    // ICONDIR
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Length);

    // ICONDIRENTRY 陣列；寬高 256 寫 0
    uint offset = (uint)(6 + 16 * images.Length);
    foreach (var (size, png) in images)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);       // colorCount
        writer.Write((byte)0);       // reserved
        writer.Write((ushort)1);     // planes
        writer.Write((ushort)32);    // bitCount
        writer.Write((uint)png.Length);
        writer.Write(offset);
        offset += (uint)png.Length;
    }

    // 圖片資料（PNG）
    foreach (var (_, png) in images)
        writer.Write(png);
}

static bool TryBuildIcns(string icnsPath)
{
    // 各尺寸方塊版：檔名 → 像素
    (string Name, int Px)[] entries =
    [
        ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32),
        ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
        ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256),
        ("icon_256x256.png", 256), ("icon_256x256@2x.png", 512),
        ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024),
    ];

    var workDir = Path.Combine(Path.GetTempPath(), "LumiIconGen-" + Guid.NewGuid().ToString("N"));
    var iconset = Path.Combine(workDir, "Lumi.iconset");
    try
    {
        Directory.CreateDirectory(iconset);
        foreach (var (name, px) in entries)
        {
            // 方塊版在各尺寸重畫（1024 基準座標等比縮放）
            var bytes = RenderPng(px, c =>
            {
                c.Scale(px / 1024f);
                DrawBadge(c);
            });
            File.WriteAllBytes(Path.Combine(iconset, name), bytes);
        }

        using var process = Process.Start(new ProcessStartInfo("iconutil")
        {
            ArgumentList = { "-c", "icns", iconset, "-o", icnsPath },
            UseShellExecute = false,
        });
        if (process is null)
            return false;
        process.WaitForExit();
        return process.ExitCode == 0;
    }
    catch (Win32Exception)
    {
        // 找不到 iconutil
        return false;
    }
    finally
    {
        try { Directory.Delete(workDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
