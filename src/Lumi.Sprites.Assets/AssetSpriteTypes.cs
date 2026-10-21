using Avalonia;
using Lumi.Sprites.Abstractions;

namespace Lumi.Sprites.Assets;

/// <summary>
/// 素材型角色的上限。載入的檔案一律當作不可信的輸入：先檢查大小與圖片標頭，再決定要不要解碼。
/// </summary>
public static class AssetSpriteLimits
{
    public const int ManifestMaxBytes = 64 * 1024;
    public const long PngMaxBytes = 8L * 1024 * 1024;

    /// <summary>圖片寬、高的上限（像素）。在解碼之前讀標頭檢查，避免壓縮炸彈。</summary>
    public const int PngMaxDimension = 4096;

    /// <summary>一個角色所有圖片解碼後（寬×高×4）的總量上限。</summary>
    public const long DecodedMaxBytes = 96L * 1024 * 1024;

    public const int FramesMax = 120;
    public const double FpsMax = 60;
    public const int NameMaxLength = 100;

    public const long PreviewMaxBytes = 1024 * 1024;
    public const int PreviewMaxDimension = 512;

    /// <summary>格子長寬比與 <c>Outer</c> 的長寬比差超過這個比例就警告。</summary>
    public const double AspectTolerance = 0.02;
}

/// <summary>manifest 裡的一個動畫（一張 sprite sheet）。</summary>
/// <param name="Key">狀態名稱（小寫，如 <c>idle</c>）或互動動畫（<c>poke.press</c>、<c>poke.release</c>、<c>drag</c>）。</param>
/// <param name="Source">manifest 裡寫的相對路徑。</param>
/// <param name="FullPath">已驗證在角色資料夾內的完整路徑。</param>
/// <param name="Columns">每列幾格；格子由左到右、由上到下排列。</param>
/// <param name="Next">播完之後接的動畫（只在不循環時有意義）。</param>
/// <param name="HoldLast">播完停在最後一格。</param>
/// <param name="SheetPixels">整張圖的像素大小（讀自標頭）。</param>
public sealed record AssetAnimation(
    string Key, string Source, string FullPath, int Frames, int Columns, double Fps, bool Loop, string? Next, bool HoldLast, Size SheetPixels)
{
    public int Rows => (Frames + Columns - 1) / Columns;

    public Size CellPixels => new(SheetPixels.Width / Columns, SheetPixels.Height / Rows);
}

/// <summary>載入時發現的問題。<see cref="Fatal"/> 的會讓角色（或該動畫）無法使用；其餘是警告，角色照常載入。</summary>
/// <param name="Folder">角色資料夾名稱。</param>
/// <param name="Field">相關的欄位（例如 <c>animations.idle.frames</c>），不一定有。</param>
public sealed record SpriteLoadIssue(string Folder, string? Field, string Message, bool Fatal)
{
    public override string ToString() => Field is null ? $"{Folder}：{Message}" : $"{Folder}（{Field}）：{Message}";
}

/// <summary>載入成功的素材型角色：manifest（交給殼與設定頁）加上播放需要的動畫資訊。</summary>
/// <param name="CellPixels">每一格的像素大小（以 <c>idle</c> 為準，所有動畫都相同）。</param>
/// <param name="PreviewPath">設定頁的縮圖（選填）。</param>
public sealed record AssetSpriteDefinition(
    SpriteManifest Manifest,
    string Directory,
    string FolderName,
    IReadOnlyDictionary<string, AssetAnimation> Animations,
    Size CellPixels,
    string? PreviewPath,
    IReadOnlyList<SpriteLoadIssue> Warnings);

/// <summary><see cref="Definition"/> 是 null 表示載入失敗（<see cref="Issues"/> 裡有 <see cref="SpriteLoadIssue.Fatal"/> 的原因）。</summary>
public sealed record SpriteLoadResult(AssetSpriteDefinition? Definition, IReadOnlyList<SpriteLoadIssue> Issues);
