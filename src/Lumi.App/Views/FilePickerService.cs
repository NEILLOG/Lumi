using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>系統的檔案／資料夾選擇器（以作用中的視窗為 owner）。</summary>
public sealed class FilePickerService : IFilePickerService
{
    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (Owner() is not { } owner)
            return [];

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickFolderAsync()
    {
        if (Owner() is not { } owner)
            return null;

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string suggestedName, string extension)
    {
        if (Owner() is not { } owner)
            return null;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private static Window? Owner()
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        return lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
    }
}
