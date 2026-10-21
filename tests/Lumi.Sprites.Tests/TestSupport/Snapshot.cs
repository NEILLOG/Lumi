using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Lumi.Sprites.Tests.TestSupport;

/// <summary>把控制項用 Skia 真的畫成點陣圖，讓測試讀每個像素（0xAARRGGBB）。</summary>
internal sealed class Snapshot
{
    private readonly byte[] _pixels;
    private readonly bool _bgra;

    private Snapshot(byte[] pixels, int width, int height, bool bgra)
    {
        _pixels = pixels;
        Width = width;
        Height = height;
        _bgra = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    /// <param name="scaling">渲染縮放（2＝Retina）；點陣圖的像素大小是 DIP 大小乘以它。</param>
    public static Snapshot Take(Control control, Size size, double scaling = 1)
    {
        control.Measure(size);
        control.Arrange(new Rect(size));
        var pixelSize = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(control);

        var stride = pixelSize.Width * 4;
        var pixels = new byte[stride * pixelSize.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(pixelSize), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        return new Snapshot(pixels, pixelSize.Width, pixelSize.Height, bitmap.Format != PixelFormat.Rgba8888);
    }

    public uint Pixel(int x, int y)
    {
        var i = (y * Width + x) * 4;
        var (r, g, b) = _bgra ? (_pixels[i + 2], _pixels[i + 1], _pixels[i]) : (_pixels[i], _pixels[i + 1], _pixels[i + 2]);
        return (uint)_pixels[i + 3] << 24 | (uint)r << 16 | (uint)g << 8 | b;
    }

    public bool IsTransparent(int x, int y) => (Pixel(x, y) >> 24) == 0;

    public IEnumerable<uint> AllPixels()
    {
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
                yield return Pixel(x, y);
        }
    }

    /// <summary>某一列裡，連續相同顏色的段落長度（用來檢查每個邏輯像素是不是一樣粗）。</summary>
    public List<int> Runs(int y, int fromX, int toX)
    {
        var runs = new List<int>();
        var count = 0;
        var previous = Pixel(fromX, y);
        for (var x = fromX; x < toX; x++)
        {
            var current = Pixel(x, y);
            if (current == previous)
                count++;
            else
            {
                runs.Add(count);
                count = 1;
                previous = current;
            }
        }
        runs.Add(count);
        return runs;
    }

    /// <summary>同 <see cref="Runs"/>，但只算有顏色（不透明）的段落：透明的留白不是邏輯像素，不該被檢查。</summary>
    public List<int> OpaqueRuns(int y, int fromX, int toX)
    {
        var runs = new List<int>();
        var count = 0;
        var previous = Pixel(fromX, y);
        for (var x = fromX; x < toX; x++)
        {
            var current = Pixel(x, y);
            if (current == previous)
            {
                count++;
                continue;
            }
            if (previous >> 24 != 0)
                runs.Add(count);
            count = 1;
            previous = current;
        }
        if (previous >> 24 != 0)
            runs.Add(count);
        return runs;
    }
}
