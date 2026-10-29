using System.Diagnostics;
using Lumi.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace Lumi.App.Services;

/// <summary>用作業系統的預設瀏覽器開啟網址（只限 http／https）。</summary>
public sealed class ProcessUrlLauncher(ILogger<ProcessUrlLauncher> logger) : IUrlLauncher
{
    public Task<bool> OpenAsync(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            return Task.FromResult(false);

        try
        {
            var start = OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open") { ArgumentList = { url.AbsoluteUri } }
                : new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
            using var process = Process.Start(start);
            return Task.FromResult(process is not null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "開啟網址 {Url} 失敗", url.GetLeftPart(UriPartial.Path));
            return Task.FromResult(false);
        }
    }
}
