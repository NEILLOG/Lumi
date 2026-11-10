using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 找出 Tab／Shift+Tab 的下一個焦點。Avalonia 12 把 <c>KeyboardNavigationHandler.GetNext</c> 收成內部，
/// 改用公開的 <see cref="IFocusManager.FindNextElement"/>，用 <see cref="FindNextElementOptions.FocusedElement"/> 指定起點（不用真的移動焦點）。
/// </summary>
internal static class TabChain
{
    public static IInputElement? GetNext(IInputElement current, NavigationDirection direction) =>
        TopLevel.GetTopLevel((Visual)current)!.FocusManager!
            .FindNextElement(direction, new FindNextElementOptions { FocusedElement = current });
}
