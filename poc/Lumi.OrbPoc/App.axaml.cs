using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Lumi.OrbPoc;

public partial class App : Application
{
    /// <summary>命令列參數：--no-clickthrough、--no-layered（僅 Windows）</summary>
    public static string[] Args { get; set; } = Array.Empty<string>();

    public static bool HasArg(string name) =>
        Args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
