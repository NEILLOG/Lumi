using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 找出 Tab／Shift+Tab 的下一個焦點。Avalonia 12 把 <c>KeyboardNavigationHandler.GetNext</c> 收成內部，
/// 而 <c>FocusManager.FindNextElement</c> 的結果和真的按 Tab 鍵不一樣，所以這裡改用真的按鍵：
/// 先把焦點放到 <paramref name="current"/>，再對視窗送 Tab／Shift+Tab，讀回實際取得焦點的控制項。
/// 起點是視窗本身時，沒有辦法用按鍵清掉目前的焦點（焦點會還原到原來的控制項），所以改取視覺樹上第一個（最後一個）
/// 可以按 Tab 停靠的控制項（略過停用與隱藏的）。起點若是停用的控制項（例如回應中的小精靈鈕）無法取得焦點，直接拋出例外，
/// 避免 Tab 鏈從錯的位置開始。
/// </summary>
internal static class TabChain
{
    public static IInputElement? GetNext(IInputElement current, NavigationDirection direction)
    {
        var top = TopLevel.GetTopLevel((Visual)current) as Window
            ?? throw new InvalidOperationException("找不到承載焦點的視窗");

        if (current is Window)
        {
            var stops = ((Visual)top).GetVisualDescendants().OfType<InputElement>().Where(IsTabStop);
            return direction == NavigationDirection.Previous ? stops.LastOrDefault() : stops.FirstOrDefault();
        }

        if (!((InputElement)current).Focus())
            throw new InvalidOperationException($"無法把焦點移到 {current.GetType().Name}（可能已停用），Tab 鏈會從錯的位置開始");
        Dispatcher.UIThread.RunJobs();

        var modifiers = direction == NavigationDirection.Previous ? RawInputModifiers.Shift : RawInputModifiers.None;
        top.KeyPress(Key.Tab, modifiers, PhysicalKey.Tab, null);
        top.KeyRelease(Key.Tab, modifiers, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        return top.FocusManager!.GetFocusedElement();
    }

    private static bool IsTabStop(InputElement e) =>
        e.Focusable && KeyboardNavigation.GetIsTabStop(e) && e.IsEffectivelyEnabled && e.IsEffectivelyVisible;
}
