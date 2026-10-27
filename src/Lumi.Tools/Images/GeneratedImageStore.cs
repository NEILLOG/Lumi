using System.Globalization;
using Lumi.Core.Context;
using Lumi.Core.Images;

namespace Lumi.Tools.Images;

/// <summary>
/// 生成或編輯的圖片存到「圖片／Lumi」（22F）。這裡的路徑是 Lumi 自己組的，不是使用者給的，所以不經過 <see cref="Lumi.Core.Tools.IFileScope"/>。
/// 檔名 <c>yyyyMMdd-HHmmss.png</c>（編輯加 <c>-edit</c>），同名加 <c>-2</c>、<c>-3</c>…。
/// </summary>
public sealed class GeneratedImageStore(string folder)
{
    public const string FolderName = "Lumi";

    /// <summary>預設位置：使用者的「圖片」資料夾底下的 Lumi。</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FolderName);

    /// <summary>存檔，回傳完整路徑。資料夾不存在就建立。</summary>
    public async Task<string> SaveAsync(GeneratedImage image, DateTimeOffset now, bool edited, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var extension = ImageFormats.Extension(image.MediaType) ?? "png";
        var stem = now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + (edited ? "-edit" : "");
        var path = Path.Combine(folder, $"{stem}.{extension}");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stem}-{n}.{extension}");

        await File.WriteAllBytesAsync(path, image.Data, ct).ConfigureAwait(false);
        return path;
    }
}
