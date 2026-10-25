using Lumi.Core.Browser;

namespace Lumi.Browser;

// 下載與等待使用者（18F）
public sealed partial class PlaywrightBrowserSessions
{
    public Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct) => throw new NotImplementedException();
}
