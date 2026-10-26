using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;

namespace Lumi.App.Views;

/// <summary>以作用中的視窗為 owner 開重新命名對話框（和 <see cref="ConfirmService"/> 同一個 owner 規則）。</summary>
public sealed class RenameService : IRenameService
{
    public async Task<string?> PromptAsync(string currentTitle, int maxLength)
    {
        var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.Windows.FirstOrDefault(w => w.IsActive) ?? lifetime?.MainWindow;
        if (owner is null)
            return null;

        return await new RenameDialog(currentTitle, maxLength).ShowDialog<string?>(owner);
    }
}
