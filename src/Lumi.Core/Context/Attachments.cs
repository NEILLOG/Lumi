namespace Lumi.Core.Context;

/// <summary>
/// 附件（目前只有圖片）的檔案存放。檔名是內容的雜湊加副檔名（相同內容只存一份）；
/// 訊息裡的 <see cref="Chat.ImageBlock"/> 只記檔名。實作在 Lumi.Infrastructure。
/// </summary>
public interface IAttachmentStore
{
    /// <summary>存進附件資料夾，回傳檔名。已經存在（相同內容）就不重複寫。</summary>
    Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default);

    /// <summary>讀出內容；檔名不合法或檔案不存在回 null（不丟例外，檔名可能來自被改過的資料庫）。</summary>
    Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default);

    /// <summary>刪除沒有被任何訊息引用的檔案，回傳刪了幾個。</summary>
    Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default);
}

/// <summary>找出所有訊息引用到的附件檔名（清理孤兒用）。</summary>
public interface IAttachmentReferences
{
    Task<IReadOnlySet<string>> GetReferencedFileNamesAsync(CancellationToken ct = default);
}

/// <summary>沒有附件儲存可用時：存不了、讀不到。</summary>
public sealed class NullAttachmentStore : IAttachmentStore
{
    public Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default) =>
        throw new NotSupportedException("沒有可用的附件儲存。");

    public Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>支援的圖片格式。判斷依檔案開頭的位元組（魔術數字），不信副檔名。</summary>
public static class ImageFormats
{
    /// <summary>Anthropic 單張圖片的上限；其他供應商也照這個。</summary>
    public const int MaxBytes = 5 * 1024 * 1024;

    public static readonly IReadOnlyList<string> MediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>認得的格式回媒體類型，否則 null。</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";
        if (data.Length >= 6 && data[..4].SequenceEqual("GIF8"u8) && (data[4] == (byte)'7' || data[4] == (byte)'9') && data[5] == (byte)'a')
            return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return null;
    }

    public static string? Extension(string mediaType) => mediaType switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };
}
