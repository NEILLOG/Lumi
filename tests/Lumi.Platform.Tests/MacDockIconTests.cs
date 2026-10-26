using Lumi.Platform.Shell;

namespace Lumi.Platform.Tests;

public class MacDockIconTests
{
    [SkippableFact]
    public void TryApply_WithGarbageBytes_ReturnsFalse_WithoutThrowing()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "Dock 圖示只在 macOS 上有意義");

        if (OperatingSystem.IsMacOS())
        {
            // 不呼叫會真的改 Dock 的成功路徑：亂位元組解不出 NSImage，應該乾淨地回傳 false
            var result = MacDockIcon.TryApply([1, 2, 3, 4]);

            Assert.False(result);
        }
    }
}
