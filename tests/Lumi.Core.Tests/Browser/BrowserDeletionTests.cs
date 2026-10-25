using Lumi.Core.Browser;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Tests.TestSupport;
using Lumi.Core.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Core.Tests.Browser;

public class BrowserDeletionTests
{
    private sealed class EmptyReferences : IAttachmentReferences
    {
        public Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    /// <summary>只記錄 <see cref="CloseAsync"/>（可設定成丟例外）；其他方法委派給 <see cref="NullBrowserSessions"/>（它是 sealed）。</summary>
    private sealed class RecordingBrowserSessions(Exception? closeFailure = null) : IBrowserSessions
    {
        private readonly NullBrowserSessions _inner = new();

        public List<string> Closed { get; } = [];

        public Task CloseAsync(string conversationId, CancellationToken ct)
        {
            Closed.Add(conversationId);
            return closeFailure is null ? Task.CompletedTask : Task.FromException(closeFailure);
        }

        public BrowserAvailability CheckAvailability() => _inner.CheckAvailability();
        public Task<BrowserOutcome> OpenAsync(string conversationId, Uri url, CancellationToken ct) => _inner.OpenAsync(conversationId, url, ct);
        public Task<BrowserSnapshot> SnapshotAsync(string conversationId, int? maxChars, CancellationToken ct) => _inner.SnapshotAsync(conversationId, maxChars, ct);
        public Task<BrowserPageContent> GetContentAsync(string conversationId, string? @ref, CancellationToken ct) => _inner.GetContentAsync(conversationId, @ref, ct);
        public Task<IReadOnlyList<BrowserTable>> GetTablesAsync(string conversationId, string? @ref, CancellationToken ct) => _inner.GetTablesAsync(conversationId, @ref, ct);
        public Task<BrowserShot> ScreenshotAsync(string conversationId, bool fullPage, CancellationToken ct) => _inner.ScreenshotAsync(conversationId, fullPage, ct);
        public Task<BrowserOutcome> ScrollAsync(string conversationId, BrowserScrollDirection direction, string? @ref, CancellationToken ct) => _inner.ScrollAsync(conversationId, direction, @ref, ct);
        public Task<BrowserOutcome> BackAsync(string conversationId, CancellationToken ct) => _inner.BackAsync(conversationId, ct);
        public Task<BrowserOutcome> ClickAsync(string conversationId, string @ref, CancellationToken ct) => _inner.ClickAsync(conversationId, @ref, ct);
        public Task<BrowserOutcome> TypeAsync(string conversationId, string @ref, string text, bool replace, bool submit, CancellationToken ct) => _inner.TypeAsync(conversationId, @ref, text, replace, submit, ct);
        public Task<BrowserOutcome> SelectAsync(string conversationId, string @ref, IReadOnlyList<string> values, CancellationToken ct) => _inner.SelectAsync(conversationId, @ref, values, ct);
        public Task<BrowserOutcome> PressAsync(string conversationId, string key, CancellationToken ct) => _inner.PressAsync(conversationId, key, ct);
        public Task<BrowserOutcome> UploadAsync(string conversationId, string @ref, string fullPath, CancellationToken ct) => _inner.UploadAsync(conversationId, @ref, fullPath, ct);
        public Task<BrowserDownload> DownloadAsync(string conversationId, string @ref, string destinationPath, long maxBytes, CancellationToken ct) => _inner.DownloadAsync(conversationId, @ref, destinationPath, maxBytes, ct);
        public Task<BrowserWaitResult> WaitForUserAsync(string conversationId, BrowserWaitCondition condition, TimeSpan timeout, CancellationToken ct) => _inner.WaitForUserAsync(conversationId, condition, timeout, ct);
        public Uri? CurrentUrl(string conversationId) => _inner.CurrentUrl(conversationId);
        public string? DescribeRef(string conversationId, string @ref) => _inner.DescribeRef(conversationId, @ref);
        public Task CloseAllAsync() => _inner.CloseAllAsync();
        public Task ClearProfileAsync(CancellationToken ct) => _inner.ClearProfileAsync(ct);
    }

    private static AttachmentCleaner Cleaner() =>
        new(new NullAttachmentStore(), new EmptyReferences(), NullLogger<AttachmentCleaner>.Instance);

    [Fact]
    public async Task Delete_closes_the_conversations_browser_tabs()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("要刪的");
        var browser = new RecordingBrowserSessions();
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(), Cleaner(), browser);

        var deleted = await service.DeleteAsync(conversation.Id);

        Assert.True(deleted);
        Assert.Equal([conversation.Id], browser.Closed);
    }

    [Fact]
    public async Task Failure_to_close_the_browser_does_not_fail_the_delete()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("要刪的");
        var memory = new ToolPermissionMemory();
        memory.Allow(conversation.Id, ToolPermission.Write);
        var browser = new RecordingBrowserSessions(new InvalidOperationException("瀏覽器掛了"));
        var service = new ConversationDeletionService(store, memory, Cleaner(), browser);

        var deleted = await service.DeleteAsync(conversation.Id);

        Assert.True(deleted);
        Assert.Single(browser.Closed);
        Assert.Empty(await store.ListAsync(100));
        Assert.False(memory.IsAllowed(conversation.Id, ToolPermission.Write));
    }

    [Fact]
    public async Task Delete_works_without_a_browser_service()
    {
        var store = new InMemoryConversationStore();
        var conversation = await store.CreateAsync("要刪的");
        var service = new ConversationDeletionService(store, new ToolPermissionMemory(), Cleaner());

        Assert.True(await service.DeleteAsync(conversation.Id));
        Assert.Empty(await store.ListAsync(100));
    }
}
