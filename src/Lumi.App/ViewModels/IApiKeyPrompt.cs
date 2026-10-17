namespace Lumi.App.ViewModels;

public enum ApiKeyPromptReason { Missing, Invalid, Unreadable }

/// <summary>
/// 請使用者輸入 API 金鑰並存進 <see cref="Lumi.Core.Secrets.ISecretStore"/>（1D 的最小對話框，M2 由設定頁取代）。
/// 金鑰不經過呼叫端：實作自己存檔，只回報有沒有存成功。
/// </summary>
public interface IApiKeyPrompt
{
    /// <returns>使用者存好金鑰回 true；取消回 false。</returns>
    Task<bool> PromptAsync(ApiKeyPromptReason reason);
}
