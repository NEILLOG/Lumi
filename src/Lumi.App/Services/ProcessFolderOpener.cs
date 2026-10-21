using System.Diagnostics;
using Lumi.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>用作業系統的預設程式開啟資料夾（Windows 的 Explorer、macOS 的 Finder）。</summary>
public sealed class ProcessFolderOpener(ILogger<ProcessFolderOpener> logger) : IFolderOpener
{
    public Task<bool> OpenAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { directory } }
                : new ProcessStartInfo(directory) { UseShellExecute = true };
            using var process = Process.Start(start);
            return Task.FromResult(process is not null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "開啟資料夾 {Directory} 失敗", directory);
            return Task.FromResult(false);
        }
    }
}
