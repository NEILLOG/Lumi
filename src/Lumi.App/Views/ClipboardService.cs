using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

public sealed class ClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
