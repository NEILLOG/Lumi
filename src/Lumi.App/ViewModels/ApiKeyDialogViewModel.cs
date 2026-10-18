using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Secrets;
using Lumi.Core.Models;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>缺金鑰時的最小輸入對話框（M2 的 2C 由設定頁取代，整個刪掉）。存完立刻清空欄位，不保留金鑰。</summary>
public partial class ApiKeyDialogViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly ConnectionSettings? _connection;
    private readonly ILogger _logger;

    /// <param name="connection">工作台目前模型的連線；金鑰存到這個連線。</param>
    public ApiKeyDialogViewModel(IModelCatalog catalog, ConnectionSettings? connection, ApiKeyPromptReason reason, ILogger logger)
    {
        _catalog = catalog;
        _connection = connection;
        _logger = logger;
        Message = reason switch
        {
            ApiKeyPromptReason.Invalid => Strings.ApiKey_Invalid,
            ApiKeyPromptReason.Unreadable => Strings.ApiKey_Unreadable,
            _ => Strings.ApiKey_Missing,
        };
    }

    /// <summary>對話框要關閉時觸發；參數是有沒有存成功。</summary>
    public event Action<bool>? CloseRequested;

    public string Message { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _apiKey = "";

    [ObservableProperty]
    private string? _error;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_connection is null)
        {
            Error = Strings.ApiKey_SaveFailed;
            return;
        }

        try
        {
            // 經過 IModelCatalog 存：金鑰進 ISecretStore，設定檔只記末 4 碼的提示
            await _catalog.SaveConnectionAsync(_connection, new SecretValue(ApiKey.Trim()));
        }
        catch (Exception ex) when (ex is SecretStoreException or IOException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "儲存 Anthropic 金鑰失敗");
            Error = Strings.ApiKey_SaveFailed;
            return;
        }

        ApiKey = "";
        CloseRequested?.Invoke(true);
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(ApiKey);

    [RelayCommand]
    private void Cancel()
    {
        ApiKey = "";
        CloseRequested?.Invoke(false);
    }
}
