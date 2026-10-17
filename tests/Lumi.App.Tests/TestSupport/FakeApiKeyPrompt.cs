using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeApiKeyPrompt(bool result) : IApiKeyPrompt
{
    public List<ApiKeyPromptReason> Calls { get; } = [];

    /// <summary>使用者「存好金鑰」時要做的事（例如把假 Provider 換成會成功的版本）。</summary>
    public Action? OnSaved { get; init; }

    public Task<bool> PromptAsync(ApiKeyPromptReason reason)
    {
        Calls.Add(reason);
        if (result)
            OnSaved?.Invoke();
        return Task.FromResult(result);
    }
}
