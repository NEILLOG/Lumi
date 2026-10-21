using Avalonia;
using Avalonia.Headless;
using Lumi.Sprites.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Lumi.Sprites.Tests;

/// <summary>
/// 不開真的視窗的 Avalonia 環境。這裡用 Skia 做真的繪製（不是 <c>UseHeadlessDrawing</c>），
/// 才能把角色畫成點陣圖檢查「畫得出東西」。
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class TestApp : Application
{
}
