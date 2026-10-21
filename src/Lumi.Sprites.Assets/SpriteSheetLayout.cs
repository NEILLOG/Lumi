using Avalonia;

namespace Lumi.Sprites.Assets;

/// <summary>sprite sheet 的切格與縮放（純計算）。</summary>
public static class SpriteSheetLayout
{
    /// <summary>第 <paramref name="index"/> 格在整張圖裡的位置（像素；由左到右、由上到下）。</summary>
    public static Rect SourceRect(Size cell, int columns, int index) =>
        new((index % columns) * cell.Width, (index / columns) * cell.Height, cell.Width, cell.Height);

    /// <summary>
    /// 一格畫在 <paramref name="outer"/>（<c>Visual</c> 的大小）裡的位置。格子等比例縮放、<b>底部置中</b>（腳底的基準線不動）。
    /// 有 <paramref name="pixelGrid"/> 時改成整數倍貼齊：每個邏輯像素畫成整數個<b>裝置像素</b>（向下取整，至少 1），
    /// 整張圖不會超出 <paramref name="outer"/>，每個邏輯像素一樣粗；位置也對齊裝置像素。
    /// </summary>
    /// <param name="cell">一格的來源像素大小。</param>
    /// <param name="pixelGrid">每一格在寬度方向的邏輯像素數（選填）。</param>
    /// <param name="renderScaling">視窗的 <c>RenderScaling</c>（1 倍、2 倍螢幕…）。</param>
    public static Rect Destination(Size outer, Size cell, int? pixelGrid, double renderScaling)
    {
        if (cell.Width <= 0 || cell.Height <= 0 || outer.Width <= 0 || outer.Height <= 0)
            return default;
        var scaling = renderScaling > 0 ? renderScaling : 1;

        if (pixelGrid is > 0)
        {
            var gridX = (double)pixelGrid.Value;
            var gridY = gridX * cell.Height / cell.Width;   // 邏輯像素是正方形
            var outerPxW = outer.Width * scaling;
            var outerPxH = outer.Height * scaling;
            var perPixel = Math.Max(1, Math.Floor(Math.Min(outerPxW / gridX, outerPxH / gridY) + 1e-9));
            var destW = gridX * perPixel;
            var destH = gridY * perPixel;
            var x = Math.Round((outerPxW - destW) / 2);
            var y = Math.Round(outerPxH - destH);
            return new Rect(x / scaling, y / scaling, destW / scaling, destH / scaling);
        }

        var scale = Math.Min(outer.Width / cell.Width, outer.Height / cell.Height);
        var width = cell.Width * scale;
        var height = cell.Height * scale;
        return new Rect((outer.Width - width) / 2, outer.Height - height, width, height);
    }
}
