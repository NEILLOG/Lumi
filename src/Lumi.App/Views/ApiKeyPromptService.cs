using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Views;

/// <summary>以主視窗為 owner 開 <see cref="ApiKeyDialog"/>。</summary>
public sealed class ApiKeyPromptService : IApiKeyPrompt
{
    private readonly ISecretStore _secrets;
    private readonly ILogger<ApiKeyPromptService> _logger;

    public ApiKeyPromptService(ISecretStore secrets, ILogger<ApiKeyPromptService> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<bool> PromptAsync(ApiKeyPromptReason reason)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return false;

        var dialog = new ApiKeyDialog(new ApiKeyDialogViewModel(_secrets, reason, _logger));
        return await dialog.ShowDialog<bool>(owner);
    }
}
