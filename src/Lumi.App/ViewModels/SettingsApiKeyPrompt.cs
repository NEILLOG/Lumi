using Lumi.App.Resources;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>
/// 缺金鑰時開啟設定視窗並定位到工作台目前模型的連線；視窗關閉後回報該連線現在有沒有金鑰
/// （有 → 工作台自動重送）。取代 M1 的最小輸入對話框。
/// </summary>
public sealed class SettingsApiKeyPrompt : IApiKeyPrompt
{
    private readonly IModelCatalog _catalog;
    private readonly ISettingsWindowService _settingsWindow;

    public SettingsApiKeyPrompt(IModelCatalog catalog, ISettingsWindowService settingsWindow)
    {
        _catalog = catalog;
        _settingsWindow = settingsWindow;
    }

    public async Task<bool> PromptAsync(ApiKeyPromptReason reason)
    {
        var connection = _catalog.Resolve(_catalog.Current.WorkbenchModelId)?.Connection;
        var notice = reason switch
        {
            ApiKeyPromptReason.Invalid => Strings.ApiKey_Invalid,
            ApiKeyPromptReason.Unreadable => Strings.ApiKey_Unreadable,
            _ => Strings.ApiKey_Missing,
        };

        // 金鑰「無效」時，使用者什麼都沒改就關掉視窗，不能算「存好了」：否則會一直重送、一直失敗。
        // 所以看視窗開著的期間設定有沒有被儲存過；缺金鑰（Missing）則只看現在有沒有金鑰。
        var saves = 0;
        void OnChanged(object? sender, AppSettings settings) => Interlocked.Increment(ref saves);
        _catalog.Changed += OnChanged;
        try
        {
            await _settingsWindow.ShowAsync(new SettingsNavigation(SettingsSection.Connections, connection?.Id, FocusApiKey: true, Notice: notice));
        }
        finally
        {
            _catalog.Changed -= OnChanged;
        }

        if (connection is null || !await _catalog.HasApiKeyAsync(connection.Id))
            return false;
        return reason == ApiKeyPromptReason.Missing || Volatile.Read(ref saves) > 0;
    }
}
