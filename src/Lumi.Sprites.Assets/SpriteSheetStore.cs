using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Lumi.Sprites.Assets;

/// <summary>解碼後的一張 sprite sheet：圖片（繪製用）加 alpha 遮罩（命中判斷用）。</summary>
internal sealed class SheetImage(Bitmap bitmap, byte[] alpha, int width, int height)
{
    public Bitmap Bitmap { get; } = bitmap;

    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>第 (x, y) 個像素的 alpha（0～255）；超出範圍回 0。</summary>
    public byte AlphaAt(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? alpha[y * Width + x] : (byte)0;
}

/// <summary>
/// 一個角色所有圖片的延遲解碼與快取：每個動畫第一次用到時才解碼（柯基 12 張圖全解開約 30 MB），之後共用。
/// 載入階段只讀標頭，解碼時才發現的問題（解碼失敗、內容貼著格子邊緣被切掉）記在 <see cref="Issues"/>。
/// 只在 UI 執行緒使用。
/// </summary>
internal sealed class SpriteSheetStore(AssetSpriteDefinition definition)
{
    private readonly Dictionary<string, SheetImage?> _sheets = new(StringComparer.Ordinal);
    private readonly List<SpriteLoadIssue> _issues = [];

    /// <summary>解碼時發現的問題（和載入時的 <see cref="AssetSpriteDefinition.Warnings"/> 分開）。</summary>
    public IReadOnlyList<SpriteLoadIssue> Issues => _issues;

    public int DecodedCount => _sheets.Values.Count(s => s is not null);

    /// <summary>取得動畫的圖；解碼失敗回 null（那個動畫畫不出來，原因記在 <see cref="Issues"/>）。</summary>
    public SheetImage? Get(AssetAnimation animation)
    {
        if (_sheets.TryGetValue(animation.FullPath, out var cached))
            return cached;

        var sheet = Decode(animation);
        _sheets[animation.FullPath] = sheet;
        if (sheet is not null)
            CheckEdges(animation, sheet);
        return sheet;
    }

    private SheetImage? Decode(AssetAnimation animation)
    {
        try
        {
            using var stream = File.OpenRead(animation.FullPath);
            var bitmap = new Bitmap(stream);
            var size = bitmap.PixelSize;
            if (size.Width != (int)animation.SheetPixels.Width || size.Height != (int)animation.SheetPixels.Height)
            {
                bitmap.Dispose();
                Report(animation, null, $"{animation.Source} 解碼後的大小 {size.Width}×{size.Height} 和標頭不一致");
                return null;
            }

            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            // 只留 alpha（RGBA 或 BGRA，alpha 都在第 4 個位元組）
            var alpha = new byte[size.Width * size.Height];
            for (var i = 0; i < alpha.Length; i++)
                alpha[i] = pixels[i * 4 + 3];
            return new SheetImage(bitmap, alpha, size.Width, size.Height);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Report(animation, null, $"{animation.Source} 解碼失敗（{ex.Message}）");
            return null;
        }
    }

    /// <summary>
    /// 內容貼著格子的左、上、右邊緣（底邊不算：腳貼底邊是設計）會被切掉，作者通常沒注意到。
    /// 離邊緣不到一個邏輯像素就警告，並列出哪幾格。只警告，不影響播放。
    /// </summary>
    private void CheckEdges(AssetAnimation animation, SheetImage sheet)
    {
        var cell = animation.CellPixels;
        var cellW = (int)cell.Width;
        var cellH = (int)cell.Height;
        var margin = Math.Max(1, (int)Math.Round(cell.Width / (definition.Manifest.PixelGrid ?? 28)));
        var touching = new List<int>();

        for (var frame = 0; frame < animation.Frames; frame++)
        {
            var rect = SpriteSheetLayout.SourceRect(cell, animation.Columns, frame);
            var left = (int)rect.X;
            var top = (int)rect.Y;
            if (TouchesEdge(sheet, left, top, cellW, cellH, margin))
                touching.Add(frame + 1);
        }

        if (touching.Count > 0)
        {
            var list = string.Join("、", touching.Take(8)) + (touching.Count > 8 ? "…" : "");
            Report(animation, "frames", $"第 {list} 格的內容離格子的左、上或右邊緣不到 {margin}px，邊緣的部分可能被切掉");
        }
    }

    private static bool TouchesEdge(SheetImage sheet, int left, int top, int width, int height, int margin)
    {
        for (var y = 0; y < height; y++)
        {
            var fullRow = y < margin;   // 上緣：整列都要看
            for (var x = 0; x < width; x++)
            {
                if (!fullRow && x >= margin && x < width - margin)
                    x = Math.Max(x, width - margin - 1);   // 中間那一段不用逐點看，直接跳到右邊緣
                else if (sheet.AlphaAt(left + x, top + y) > 0)
                    return true;
            }
        }
        return false;
    }

    private void Report(AssetAnimation animation, string? field, string message) =>
        _issues.Add(new SpriteLoadIssue(definition.FolderName, $"animations.{animation.Key}" + (field is null ? "" : "." + field), message, false));
}
