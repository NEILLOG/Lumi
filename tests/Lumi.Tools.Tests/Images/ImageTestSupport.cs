using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Images;
using Lumi.Core.Models;
using Lumi.Core.Tools;
using Lumi.Tools.Tests.Memory;

namespace Lumi.Tools.Tests.Images;

/// <summary>假的圖片生成：記錄請求，回傳預先設定的圖片或丟例外。</summary>
internal sealed class FakeImageGenerator : IImageGenerator
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2];
    public static readonly byte[] Gif = [.. "GIF89a"u8, 0, 0];

    public List<ImageGenerationRequest> Generations { get; } = [];

    public List<ImageEditRequest> Edits { get; } = [];

    public GeneratedImage Result { get; set; } = new(Png, "image/png", null);

    public Exception? Failure { get; set; }

    public Task<GeneratedImage> GenerateAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        Generations.Add(request);
        if (Failure is not null)
            throw Failure;
        return Task.FromResult(Result);
    }

    public Task<GeneratedImage> EditAsync(ImageEditRequest request, CancellationToken ct = default)
    {
        Edits.Add(request);
        if (Failure is not null)
            throw Failure;
        return Task.FromResult(Result);
    }
}

/// <summary>對話的訊息（只實作 <see cref="GetMessagesAsync"/>；圖片工具只讀訊息）。</summary>
internal sealed class MessageConversations : IConversationStore
{
    public List<ChatMessage> Messages { get; } = [];

    public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(string conversationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Messages.ToList());

    public Task<Conversation> CreateAsync(string title, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Conversation?> GetLatestAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Conversation>> ListAsync(int limit, ConversationCursor? before, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AppendMessageAsync(string conversationId, ChatMessage message, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> RemoveLastMessageAsync(string conversationId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RenameAsync(string conversationId, string title, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, int limit, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>附件儲存的假實作：只有讀取。</summary>
internal sealed class MemoryAttachments : IAttachmentStore
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) => Task.FromResult(Files.GetValueOrDefault(fileName));

    public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>固定時間的 <see cref="TimeProvider"/>：同一秒產生兩張圖時檔名會重複，測試同名加編號。</summary>
internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}

/// <summary>圖片工具測試共用：已設定的目錄（連線 gw、模型 gpt-image-2）、上下文、固定時間。</summary>
internal static class ImageTestSupport
{
    public static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 28, 9, 30, 15, DateTimeKind.Local));

    public static FakeModelCatalog ConfiguredCatalog(string size = "1024x1024")
    {
        var connection = new ConnectionSettings("gw", "公司 Gateway", ProviderFormat.OpenAICompatible, "https://gateway.example/v1");
        var catalog = new FakeModelCatalog();
        catalog.Current = catalog.Current with
        {
            Connections = [connection],
            ImageGeneration = new ImageGenerationSettings(connection.Id, "gpt-image-2", size),
        };
        return catalog;
    }

    public static ToolContext ImageContext(IFileScope scope, TimeProvider? time = null) =>
        new("c1", ChatMode.Workbench, time ?? new FixedTime(Now), scope);

    public static ChatMessage UserWith(params ContentBlock[] blocks) =>
        new(ChatMessage.NewId(), ChatRole.User, blocks, Now);

    public static ChatMessage ToolWith(params ToolResultBlock[] blocks) =>
        new(ChatMessage.NewId(), ChatRole.Tool, blocks, Now);

    public static ToolResultBlock ProducedImage(params string[] paths) =>
        new("t1", "已產生圖片", false, "產生圖片", null, "已產生圖片", 1, ToolApproval.AllowedOnce, null, paths);
}
