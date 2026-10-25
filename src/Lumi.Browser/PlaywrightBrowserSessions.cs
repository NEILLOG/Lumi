using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace Lumi.Browser;

/// <summary>
/// 用 Playwright 實作的瀏覽器工作階段（M18）。拆成四個 partial 檔，各由一個包負責：
/// 本檔（生命週期，18C）、<c>.Navigation.cs</c>（18D）、<c>.Actions.cs</c>（18E）、<c>.Transfer.cs</c>（18F）。
/// Phase 0 只有骨架，所有方法先丟 <see cref="NotImplementedException"/>。
/// </summary>
public sealed partial class PlaywrightBrowserSessions : IBrowserSessions
{
    private readonly IBrowserLocator _locator;
    private readonly IModelCatalog _catalog;
    private readonly ISecretStore _secrets;
    private readonly BrowserOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PlaywrightBrowserSessions> _logger;

    public PlaywrightBrowserSessions(
        IBrowserLocator locator, IModelCatalog catalog, ISecretStore secrets, BrowserOptions options, TimeProvider time,
        ILogger<PlaywrightBrowserSessions> logger)
    {
        _locator = locator;
        _catalog = catalog;
        _secrets = secrets;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public BrowserAvailability CheckAvailability() => throw new NotImplementedException();

    public Uri? CurrentUrl(string conversationId) => throw new NotImplementedException();

    public string? DescribeRef(string conversationId, string @ref) => throw new NotImplementedException();

    public Task CloseAsync(string conversationId, CancellationToken ct) => throw new NotImplementedException();

    public Task CloseAllAsync() => throw new NotImplementedException();

    public Task ClearProfileAsync(CancellationToken ct) => throw new NotImplementedException();
}
