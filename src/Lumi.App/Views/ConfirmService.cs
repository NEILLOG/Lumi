using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>以目前作用中的視窗為 owner 開確認視窗（設定視窗裡要確認時 owner 就是設定視窗）。</summary>
public sealed class ConfirmService : IConfirmService
{
    public Task<bool> ConfirmAsync(string message, string confirmText) => ShowAsync(message, confirmText, destructive: false);

    public Task<bool> ConfirmDestructiveAsync(string message, string confirmText) => ShowAsync(message, confirmText, destructive: true);

    private static async Task<bool> ShowAsync(string message, string confirmText, bool destructive)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
        if (owner is null)
            return false;

        return await new ConfirmDialog(message, confirmText, destructive).ShowDialog<bool>(owner);
    }
}
