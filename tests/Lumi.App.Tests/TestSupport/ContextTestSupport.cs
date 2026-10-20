using Lumi.App.ViewModels;
using Lumi.Core.Context;

namespace Lumi.App.Tests.TestSupport;

internal sealed class InMemoryConversationContextStore : IConversationContextStore
{
    private readonly List<(string ConversationId, ContextItem Item)> _items = [];

    public Task<IReadOnlyList<ContextItem>> GetAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContextItem>>(_items.Where(i => i.ConversationId == conversationId).Select(i => i.Item).ToList());

    public Task<ContextItem> AddAsync(string conversationId, ContextKind kind, string path, string displayName, CancellationToken ct = default)
    {
        var index = _items.FindIndex(i => i.ConversationId == conversationId && i.Item.Kind == kind && i.Item.Path == path);
        if (index >= 0)
        {
            var existing = _items[index].Item;
            var reset = existing.IsPending ? existing : existing with { EmbeddedInMessageId = null };
            _items[index] = (conversationId, reset);
            return Task.FromResult(reset);
        }
        var item = new ContextItem(ContextItem.NewId(), kind, path, displayName, DateTimeOffset.UtcNow, null);
        _items.Add((conversationId, item));
        return Task.FromResult(item);
    }

    public Task RemoveAsync(string conversationId, string itemId, CancellationToken ct = default)
    {
        _items.RemoveAll(i => i.ConversationId == conversationId && i.Item.Id == itemId);
        return Task.CompletedTask;
    }

    public Task MarkEmbeddedAsync(string conversationId, IReadOnlyCollection<string> itemIds, string messageId, CancellationToken ct = default)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].ConversationId == conversationId && itemIds.Contains(_items[i].Item.Id))
                _items[i] = (conversationId, _items[i].Item with { EmbeddedInMessageId = messageId });
        }
        return Task.CompletedTask;
    }
}

/// <summary>依路徑回傳預先設定的種類與狀態；沒設定過的路徑視為找不到。</summary>
internal sealed class FakeContextInspector : IContextInspector
{
    private readonly Dictionary<string, (ContextKind Kind, ContextItemStatus Status)> _known = [];

    public FakeContextInspector Know(string path, ContextKind kind, ContextItemStatus? status = null)
    {
        _known[path] = (kind, status ?? new ContextItemStatus(ContextItemProblem.None));
        return this;
    }

    public void SetStatus(string path, ContextItemStatus status) => _known[path] = (_known[path].Kind, status);

    public Task<ContextKind?> ClassifyAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<ContextKind?>(_known.TryGetValue(path, out var known) ? known.Kind : null);

    public Task<ContextItemStatus> InspectAsync(ContextKind kind, string path, CancellationToken ct = default) =>
        Task.FromResult(_known.TryGetValue(path, out var known) ? known.Status : new ContextItemStatus(ContextItemProblem.Missing));
}

internal sealed class FakeFilePicker : IFilePickerService
{
    public List<string> Files { get; } = [];

    public string? Folder { get; set; }

    public int FilePicks { get; private set; }

    public int FolderPicks { get; private set; }

    public Task<IReadOnlyList<string>> PickFilesAsync()
    {
        FilePicks++;
        return Task.FromResult<IReadOnlyList<string>>(Files.ToList());
    }

    public Task<string?> PickFolderAsync()
    {
        FolderPicks++;
        return Task.FromResult(Folder);
    }
}

internal sealed class FakeClipboardImage : IClipboardImageService
{
    public ClipboardImage? Image { get; set; }

    public Task<ClipboardImage?> TryGetImageAsync() => Task.FromResult(Image);
}

internal sealed class FakeAttachmentStore : IAttachmentStore
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public bool Throw { get; set; }

    public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
    {
        if (Throw)
            throw new ArgumentException("不支援");
        var name = $"{Files.Count + 1:D64}.png";
        Files[name] = data.ToArray();
        return Task.FromResult(name);
    }

    public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) => Task.FromResult(Files.GetValueOrDefault(fileName));

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);

    public string? GetPath(string fileName) => "/attachments/" + fileName;
}

/// <summary>估算固定值的建構器；Build 依待送出項目產生說明用的區塊並標記為已處理。</summary>
internal sealed class FakeContextBuilder : IContextAttachmentBuilder
{
    public int PendingTokensPerItem { get; set; } = 100;

    public Task<ContextAttachments> BuildAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default) =>
        Task.FromResult(new ContextAttachments(
            pendingItems.Select(i => (Lumi.Core.Chat.ContentBlock)new Lumi.Core.Chat.ContextBlock(
                i.Kind, i.DisplayName, i.Path, "內容 " + i.DisplayName, true, null, i.Kind == ContextKind.Folder ? 7 : null)).ToList(),
            pendingItems.Select(i => i.Id).ToList()));

    public Task<int> EstimateTokensAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default) =>
        Task.FromResult(pendingItems.Count * PendingTokensPerItem);
}
