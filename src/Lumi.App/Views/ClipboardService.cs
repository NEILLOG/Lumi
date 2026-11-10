using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Lumi.App.Markdown;
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

    public async Task SetMarkdownAsync(string markdown)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is { } clipboard)
            await MarkdownClipboard.SetAsync(clipboard, markdown);
    }
}
