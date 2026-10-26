namespace Lumi.App.ViewModels;

/// <summary>系統的檔案／資料夾選擇器。ViewModel 不碰 Avalonia，測試用假的。</summary>
public interface IFilePickerService
{
    /// <summary>選擇一個或多個檔案；使用者取消回空清單。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync();

    /// <summary>選擇一個資料夾；使用者取消回 null。</summary>
    Task<string?> PickFolderAsync();

    /// <summary>選擇要儲存的檔案位置（<paramref name="extension"/> 不含句點，例如 <c>md</c>）；使用者取消回 null。</summary>
    Task<string?> SaveFileAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);
}

/// <param name="MediaType">依內容判斷（例如 <c>image/png</c>）。</param>
public sealed record ClipboardImage(byte[] Data, string MediaType);

/// <summary>讀取剪貼簿裡的圖片（貼上截圖）。沒有圖片回 null。</summary>
public interface IClipboardImageService
{
    Task<ClipboardImage?> TryGetImageAsync();
}

internal sealed class NullFilePickerService : IFilePickerService
{
    public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}

internal sealed class NullClipboardImageService : IClipboardImageService
{
    public Task<ClipboardImage?> TryGetImageAsync() => Task.FromResult<ClipboardImage?>(null);
}
