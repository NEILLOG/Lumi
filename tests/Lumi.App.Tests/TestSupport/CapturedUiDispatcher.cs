using Lumi.App.ViewModels;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 開著真視窗的測試用的派送器：和正式程式一樣，把背景執行緒來的更新丟回 UI 執行緒。
/// 不用 <c>AvaloniaUiDispatcher</c>：背景執行緒不能碰 <c>Dispatcher.UIThread</c>，Avalonia 在測試之間會重設它，
/// 這時取值會建出一個不能跑迴圈的 Dispatcher，下一個測試就壞了。
/// </summary>
internal sealed class CapturedUiDispatcher(SynchronizationContext context) : IUiDispatcher
{
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    public void Post(Action action)
    {
        if (Environment.CurrentManagedThreadId == _threadId)
            action();
        else
            context.Post(_ => action(), null);
    }
}
