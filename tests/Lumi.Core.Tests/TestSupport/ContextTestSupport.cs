using Lumi.Core.Context;
using Lumi.Core.Tools;

namespace Lumi.Core.Tests.TestSupport;

internal sealed class InMemoryConversationContextStore : IConversationContextStore
{
    private readonly List<(string ConversationId, ContextItem Item)> _items = [];

    public Task<IReadOnlyList<ContextItem>> GetAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContextItem>>(_items.Where(i => i.ConversationId == conversationId).Select(i => i.Item).ToList());

    public Task<ContextItem> AddAsync(string conversationId, ContextKind kind, string path, string displayName, CancellationToken ct = default)
    {
        var existing = _items.FindIndex(i => i.ConversationId == conversationId && i.Item.Kind == kind && i.Item.Path == path);
        if (existing >= 0)
        {
            var reset = _items[existing].Item with { EmbeddedInMessageId = null };
            _items[existing] = (conversationId, reset);
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

/// <summary>簡化的範圍：資料夾項目的路徑開頭符合才算在範圍內（不碰檔案系統，Core 測試用）。</summary>
internal sealed class PrefixFileScopeFactory : IFileScopeFactory
{
    public IFileScope Create(IReadOnlyList<ContextItem> items)
    {
        var roots = items.Where(i => i.Kind != ContextKind.Image).Select(i => new ScopeRoot(i.DisplayName, i.Path, i.Kind == ContextKind.Folder)).ToList();
        return roots.Count == 0 ? EmptyFileScope.Instance : new PrefixFileScope(roots);
    }

    private sealed class PrefixFileScope(IReadOnlyList<ScopeRoot> roots) : IFileScope
    {
        public IReadOnlyList<ScopeRoot> Roots => roots;

        public ScopeResolution Resolve(string path) => roots.FirstOrDefault(r => path.StartsWith(r.Path, StringComparison.Ordinal)) is { } root
            ? new ScopeResolution(path, root, null)
            : ScopeResolution.Denied($"路徑不在可存取的範圍內：{path}");

        public string ToDisplayPath(string fullPath) => fullPath;
    }
}

/// <summary>需要範圍的假工具：把引數當路徑，用 <see cref="ToolContext.Scope"/> 解析。</summary>
public sealed class ScopedTool : ToolBase<EchoArgs>
{
    public override ToolDescriptor Descriptor { get; } = new(
        "scoped_read", "讀範圍內的檔案", "測試用：需要範圍。", ToolPermission.ReadOnly, ToolModes.Both, TimeSpan.FromSeconds(5), RequiresScope: true);

    public List<string> Resolved { get; } = [];

    protected override Task<ToolResult> InvokeAsync(EchoArgs args, ToolContext context, CancellationToken ct)
    {
        var resolution = context.Scope.Resolve(args.Text);
        if (!resolution.IsAllowed)
            return Task.FromResult(ToolResult.Error(resolution.Error!, "不在範圍內"));
        Resolved.Add(resolution.FullPath!);
        return Task.FromResult(ToolResult.Ok("內容：" + resolution.FullPath, "已讀取"));
    }
}
