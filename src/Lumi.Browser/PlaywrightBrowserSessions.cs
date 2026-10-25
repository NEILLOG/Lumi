using Lumi.Core.Browser;
using Lumi.Core.Models;
using Lumi.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace Lumi.Browser;

/// <summary>
/// 用 Playwright 實作的瀏覽器工作階段（M18）。拆成四個 partial 檔，各由一個包負責：
/// 本檔（生命週期，18C）、<c>.Navigation.cs</c>（18D）、<c>.Actions.cs</c>（18E）、<c>.Transfer.cs</c>（18F）。
/// 程序與分頁的管理都在 <see cref="BrowserHost"/>；Wave 2 的 partial 檔直接用 <c>_host.GetStateAsync(...)</c>、<c>_host.EnsureVisibleAsync(...)</c>。
/// </summary>
public sealed partial class PlaywrightBrowserSessions : IBrowserSessions, IAsyncDisposable
{
    private readonly IBrowserLocator _locator;
    private readonly IModelCatalog _catalog;
    private readonly ISecretStore _secrets;
    private readonly BrowserOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PlaywrightBrowserSessions> _logger;
    private readonly BrowserHost _host;

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
        _host = new BrowserHost(locator, catalog, secrets, options, time, logger);
    }

    public BrowserAvailability CheckAvailability() => _locator.Resolve(_catalog.Current.Browser.Choice);

    /// <summary>目前分頁的網址；不是 http／https（例如 about:blank）或沒有分頁時 null。</summary>
    public Uri? CurrentUrl(string conversationId)
    {
        var url = _host.TryGetState(conversationId)?.PageOrNull?.Url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    public string? DescribeRef(string conversationId, string @ref) =>
        _host.TryGetState(conversationId)?.RefLabels.GetValueOrDefault(@ref);

    public Task CloseAsync(string conversationId, CancellationToken ct) => _host.CloseConversationAsync(conversationId, ct);

    public Task CloseAllAsync() => _host.CloseAllAsync();

    public Task ClearProfileAsync(CancellationToken ct) => _host.ClearProfileAsync(ct);

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
