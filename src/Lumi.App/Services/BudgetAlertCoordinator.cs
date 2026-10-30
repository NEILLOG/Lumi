using Lumi.App.Resources;
using Lumi.App.ViewModels;
using Lumi.Core.Chat;
using Lumi.Core.Usage;

namespace Lumi.App.Services;

/// <summary>
/// 小精靈說一句（<see cref="BudgetAlertCoordinator"/> 只依賴這個介面；實作由整合包接到小精靈的氣泡）。
/// </summary>
public interface ISpriteNotice
{
    void Say(string text);
}

/// <summary>
/// 每月預算提醒的接線（plan/23-usage-cost.md 第 5 節）：每次回應結束（<see cref="IChatSession.RunFinished"/>）檢查一次，
/// 跨過門檻就依作用中的模式顯示：工作台模式是工作台頂端的提醒列，小精靈模式是小精靈說一句。
/// 門檻是否已提醒過由 <see cref="BudgetMonitor"/> 記錄，這裡不重複判斷。
/// </summary>
public sealed class BudgetAlertCoordinator
{
    private readonly IChatSession _session;
    private readonly BudgetMonitor _monitor;
    private readonly BudgetAlertViewModel _workbenchBanner;
    private readonly ISpriteNotice? _sprite;
    private readonly IUiDispatcher _dispatcher;

    public BudgetAlertCoordinator(IChatSession session, BudgetMonitor monitor, BudgetAlertViewModel workbenchBanner, ISpriteNotice? sprite, IUiDispatcher dispatcher)
    {
        _session = session;
        _monitor = monitor;
        _workbenchBanner = workbenchBanner;
        _sprite = sprite;
        _dispatcher = dispatcher;
        _session.RunFinished += OnRunFinished;
    }

    private void OnRunFinished(object? sender, RunFinishedArgs args) => args.Defer(CheckAsync());

    private async Task CheckAsync()
    {
        var level = await _monitor.CheckAsync();
        if (level is not { } reached)
            return;

        var (spent, budget) = await _monitor.GetMonthAsync();
        if (budget is not { } limit)
            return;

        // 模式在顯示的那一刻才決定（UI 執行緒上讀），這時的作用中模式才是使用者看得到的
        _dispatcher.Post(() => Notify(reached, spent, limit));
    }

    private void Notify(BudgetAlertLevel level, decimal spentUsd, decimal budgetUsd)
    {
        if (_session.ActiveMode == ChatMode.Sprite)
        {
            _sprite?.Say(level == BudgetAlertLevel.Hundred ? Strings.Sprite_Budget100 : Strings.Sprite_Budget80);
            return;
        }

        _workbenchBanner.Show(level, spentUsd, budgetUsd);
    }
}
