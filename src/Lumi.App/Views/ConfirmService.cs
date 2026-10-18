using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>以目前作用中的視窗為 owner 開確認視窗（設定視窗裡要確認時 owner 就是設定視窗）。</summary>
public sealed class ConfirmService : IConfirmService
{
    public async Task<bool> ConfirmAsync(string message, string confirmText)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
        if (owner is null)
            return false;

        return await new ConfirmDialog(message, confirmText).ShowDialog<bool>(owner);
    }
}
