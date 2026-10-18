using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Views;

/// <summary>以主視窗為 owner 開 <see cref="ApiKeyDialog"/>。</summary>
public sealed class ApiKeyPromptService : IApiKeyPrompt
{
    private readonly IModelCatalog _catalog;
    private readonly ILogger<ApiKeyPromptService> _logger;

    public ApiKeyPromptService(IModelCatalog catalog, ILogger<ApiKeyPromptService> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public async Task<bool> PromptAsync(ApiKeyPromptReason reason)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
            return false;

        var connection = _catalog.Resolve(_catalog.Current.WorkbenchModelId)?.Connection;
        var dialog = new ApiKeyDialog(new ApiKeyDialogViewModel(_catalog, connection, reason, _logger));
        return await dialog.ShowDialog<bool>(owner);
    }
}
