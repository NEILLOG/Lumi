using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Lumi.App.Sprites;

/// <summary>
/// 托盤圖示：讀取 <c>Assets/Icons/lumi-tray-64.png</c>（M20 20E，由 tools/Lumi.IconGen 產生的光球）。不分深淺色版本。
/// </summary>
public static class TrayIconFactory
{
    private static readonly Uri TrayIconUri = new("avares://Lumi.App/Assets/Icons/lumi-tray-64.png");

    /// <summary>
    /// <paramref name="size"/> 參數保留但不使用：圖檔固定 64px，系統會自行縮放。
    /// </summary>
    public static WindowIcon CreateOrbIcon(int size = 64)
    {
        using var stream = AssetLoader.Open(TrayIconUri);
        return new WindowIcon(new Bitmap(stream));
    }
}
