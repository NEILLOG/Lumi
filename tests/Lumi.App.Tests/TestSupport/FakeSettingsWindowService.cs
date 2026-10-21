using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeSettingsWindowService : ISettingsWindowService
{
    public List<SettingsNavigation> Shown { get; } = [];

    /// <summary>「視窗開著的期間」使用者做的事（例如存好金鑰）。</summary>
    public Func<Task>? WhileOpen { get; set; }

    /// <summary>「回到對話」被呼叫的次數。</summary>
    public int ReturnToWorkbenchCalls { get; private set; }

    /// <summary>「回到對話」的結果（false = 使用者在未儲存變更詢問時取消）。</summary>
    public bool ReturnToWorkbenchResult { get; set; } = true;

    public Task<bool> ReturnToWorkbench()
    {
        ReturnToWorkbenchCalls++;
        return Task.FromResult(ReturnToWorkbenchResult);
    }

    public async Task ShowAsync(SettingsNavigation navigation)
    {
        Shown.Add(navigation);
        if (WhileOpen is not null)
            await WhileOpen();
    }
}
