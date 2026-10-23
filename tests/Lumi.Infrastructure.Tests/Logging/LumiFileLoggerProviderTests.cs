using Lumi.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Tests.Logging;

public class LumiFileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lumi-log-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 23, 9, 30, 0, TimeSpan.FromHours(8));

    private LumiFileLoggerProvider NewProvider() => new(_directory, () => _now);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void WritesOneLinePerEntry_IntoTheDailyFile()
    {
        var provider = NewProvider();

        provider.CreateLogger("Lumi.Test").LogInformation("第一行");
        provider.CreateLogger("Lumi.Test").LogError("第二行");

        var lines = File.ReadAllLines(Path.Combine(_directory, "lumi-2026-10-23.log"));
        Assert.Equal("2026-10-23 09:30:00.000 [Information] Lumi.Test: 第一行", lines[0]);
        Assert.Equal("2026-10-23 09:30:00.000 [Error] Lumi.Test: 第二行", lines[1]);
    }

    [Fact]
    public void Exceptions_AreWrittenAfterTheMessage()
    {
        var provider = NewProvider();

        provider.CreateLogger("Lumi.Test").LogCritical(new InvalidOperationException("壞掉了"), "啟動失敗");

        var text = File.ReadAllText(provider.CurrentFilePath);
        Assert.Contains("[Critical] Lumi.Test: 啟動失敗", text);
        Assert.Contains("InvalidOperationException: 壞掉了", text);
    }

    [Fact]
    public void DebugLevel_IsNotWritten()
    {
        var provider = NewProvider();

        provider.CreateLogger("Lumi.Test").LogDebug("只給開發者");

        Assert.False(File.Exists(provider.CurrentFilePath));
    }

    [Fact]
    public void MicrosoftCategories_OnlyWriteWarningAndAbove()
    {
        var provider = NewProvider();
        var logger = provider.CreateLogger("Microsoft.Extensions.Hosting");

        logger.LogInformation("啟動完成");
        Assert.False(File.Exists(provider.CurrentFilePath));

        logger.LogWarning("注意");
        Assert.Contains("[Warning] Microsoft.Extensions.Hosting: 注意", File.ReadAllText(provider.CurrentFilePath));
    }

    [Fact]
    public void WriteFailure_IsSwallowed()
    {
        // 目錄的位置被一個檔案佔住：建立資料夾會失敗，記錄不能讓程式出錯
        File.WriteAllText(_directory, "佔位");
        try
        {
            var provider = NewProvider();

            var ex = Record.Exception(() => provider.CreateLogger("Lumi.Test").LogError("寫不進去"));

            Assert.Null(ex);
        }
        finally
        {
            File.Delete(_directory);
        }
    }
}
