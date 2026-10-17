using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Secrets;
using Lumi.Providers.Anthropic;
using Microsoft.Extensions.Logging;

namespace Lumi.App.ViewModels;

/// <summary>缺金鑰時的最小輸入對話框（M2 由設定頁取代，整個刪掉）。存完立刻清空欄位，不保留金鑰。</summary>
public partial class ApiKeyDialogViewModel : ObservableObject
{
    private readonly ISecretStore _secrets;
    private readonly ILogger _logger;

    public ApiKeyDialogViewModel(ISecretStore secrets, ApiKeyPromptReason reason, ILogger logger)
    {
        _secrets = secrets;
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
        try
        {
            await _secrets.SetAsync(AnthropicDevOptions.ApiKeySecret, new SecretValue(ApiKey.Trim()));
        }
        catch (SecretStoreException ex)
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
