using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lumi.App.Tests.TestSupport;

/// <summary>
/// 工作台拆成 UserControl 之後，x:Name 各自在 UserControl 的 name scope 裡，<c>window.FindControl</c> 找不到。
/// 測試改用這個 helper 沿著視覺樹找。
/// </summary>
public static class VisualFinder
{
    public static T FindByName<T>(this Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
