using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.Providers;

/// <summary>兩家 Provider 共用：送出前把請求裡用到的圖片讀進記憶體（訊息只存檔名，位元組在附件資料夾）。</summary>
internal static class ImageAttachments
{
    public static readonly IReadOnlyDictionary<string, byte[]> None = new Dictionary<string, byte[]>();

    public static async Task<IReadOnlyDictionary<string, byte[]>> LoadAsync(
        IAttachmentStore store, IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        var names = messages.SelectMany(m => m.Content).SelectMany(ImagesOf).Select(b => b.FileName).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            return None;

        var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            // 讀不到（檔案被刪、檔名不合法）：略過，轉換時改成一段說明文字，不讓整個請求失敗
            if (await store.ReadAsync(name, ct).ConfigureAwait(false) is { } bytes)
                loaded[name] = bytes;
        }
        return loaded;
    }

    public static string MissingText(ImageBlock block) => $"[圖片「{block.DisplayName}」已經遺失，沒有送出]";

    public static bool HasImages(ChatRequest request) =>
        request.Messages.Any(m => m.Content.Any(b => ImagesOf(b).Any()));

    /// <summary>一個內容區塊帶的圖片：圖片區塊本身，或工具結果上附的圖片（M18 D12）。</summary>
    public static IEnumerable<ImageBlock> ImagesOf(ContentBlock block) => block switch
    {
        ImageBlock image => [image],
        ToolResultBlock { Images: { } images } => images,
        _ => [],
    };
}
