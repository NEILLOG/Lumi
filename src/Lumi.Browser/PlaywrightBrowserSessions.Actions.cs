using Lumi.Core.Browser;

namespace Lumi.Browser;

// 點擊與輸入（18E）
public sealed partial class PlaywrightBrowserSessions
{
    public Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct) => throw new NotImplementedException();

    public Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct) => throw new NotImplementedException();
}
