using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 找出 Tab／Shift+Tab 的下一個焦點。Avalonia 12 把 <c>KeyboardNavigationHandler.GetNext</c> 收成內部，
/// 改用公開的 <see cref="IFocusManager.FindNextElement"/>（從目前焦點出發，所以先把焦點移到 <paramref name="current"/>）。
/// </summary>
internal static class TabChain
{
    // TODO(33A)：這是升級時的暫時寫法，Tab 順序的測試還有失敗；確認走訪方式正確後拿掉這行
    public static IInputElement? GetNext(IInputElement current, NavigationDirection direction)
    {
        current.Focus();
        return TopLevel.GetTopLevel((Visual)current)!.FocusManager!.FindNextElement(direction);
    }
}
