using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

internal sealed class FakeSettingsWindowService : ISettingsWindowService
{
    public List<SettingsNavigation> Shown { get; } = [];

    /// <summary>「視窗開著的期間」使用者做的事（例如存好金鑰）。</summary>
    public Func<Task>? WhileOpen { get; set; }

    public async Task ShowAsync(SettingsNavigation navigation)
    {
        Shown.Add(navigation);
        if (WhileOpen is not null)
            await WhileOpen();
    }
}
