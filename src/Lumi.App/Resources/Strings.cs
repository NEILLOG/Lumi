using System.Globalization;
using System.Resources;

namespace Lumi.App.Resources;

/// <summary>介面文字的存取點，XAML 用 {x:Static res:Strings.Xxx} 引用。對應 Strings.resx。</summary>
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("Lumi.App.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string name) =>
        Manager.GetString(name, CultureInfo.CurrentUICulture) ?? $"!{name}!";

    public static string Workbench_Title => Get(nameof(Workbench_Title));
    public static string Workbench_MessageArea => Get(nameof(Workbench_MessageArea));
    public static string Workbench_InputArea => Get(nameof(Workbench_InputArea));
    public static string Workbench_InputPlaceholder => Get(nameof(Workbench_InputPlaceholder));
    public static string Workbench_StatusReady => Get(nameof(Workbench_StatusReady));
    public static string Workbench_DemoText => Get(nameof(Workbench_DemoText));
}
