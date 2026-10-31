using System.Text;
using Lumi.Core.Chat;
using Lumi.Core.Context;
using Lumi.Core.Reminders;
using Lumi.Core.Usage;

namespace Lumi.Tools;

/// <summary>
/// <see cref="IContextAttachmentBuilder"/> 的實作：把待送出的項目拍成快照（M4 的 Q1）。
/// 讀不到、太大、不是文字檔都不丟例外，而是產生一個說明原因的區塊；檔案與資料夾仍在範圍內，模型可以用工具按需讀。
/// </summary>
/// <param name="runs">排程紀錄（引用卡要讀最後的回答）；沒有時引用卡說明讀不到。</param>
/// <param name="conversations">排程紀錄的對話（同上）。</param>
public sealed class ContextAttachmentBuilder(
    FileToolOptions options,
    IAttachmentStore attachments,
    ITokenEstimator estimator,
    IImageDownscaler? downscaler = null,
    IScheduledRunStore? runs = null,
    IConversationStore? conversations = null) : IContextAttachmentBuilder
{
    /// <summary>圖片長邊上限（Anthropic 建議值）；超過會被伺服器縮小但照樣計費，所以存附件前先縮（L5）。</summary>
    public const int MaxImageEdge = 1568;

    public Task<ContextAttachments> BuildAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default) =>
        Task.Run(() => BuildCoreAsync(pendingItems, saveImages: true, ct), ct);

    public async Task<int> EstimateTokensAsync(IReadOnlyList<ContextItem> pendingItems, CancellationToken ct = default)
    {
        var built = await Task.Run(() => BuildCoreAsync(pendingItems, saveImages: false, ct), ct).ConfigureAwait(false);
        return built.Blocks.Count == 0
            ? 0
            : estimator.EstimateMessages([new ChatMessage("estimate", ChatRole.User, built.Blocks, DateTimeOffset.UtcNow)]);
    }

    private async Task<ContextAttachments> BuildCoreAsync(IReadOnlyList<ContextItem> items, bool saveImages, CancellationToken ct)
    {
        var blocks = new List<ContentBlock>();
        var ids = new List<string>();
        long embeddedBytes = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            ContentBlock block;
            try
            {
                block = item.Kind switch
                {
                    ContextKind.File => BuildFile(item, ref embeddedBytes),
                    ContextKind.Folder => BuildFolder(item, ct),
                    ContextKind.Image => await BuildImageAsync(item, saveImages, ct).ConfigureAwait(false),
                    ContextKind.ScheduledRun => await BuildScheduledRunAsync(item, ct).ConfigureAwait(false),
                    _ => Missing(item, "不認得的項目種類"),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                block = Missing(item, "無法讀取（沒有權限，或檔案正被其他程式占用）");
            }

            blocks.Add(block);
            ids.Add(item.Id);
        }
        return new ContextAttachments(blocks, ids);
    }

    private static ContextBlock Missing(ContextItem item, string note) =>
        new(item.Kind, item.DisplayName, item.Path, null, false, note);

    private ContentBlock BuildFile(ContextItem item, ref long embeddedBytes)
    {
        if (Directory.Exists(item.Path))
            return Missing(item, "這是資料夾，不是檔案");
        if (!File.Exists(item.Path))
            return Missing(item, "找不到這個檔案（可能已被移動或刪除）");

        // Office／PDF 不嵌入（即使很小），由模型用 read_document 讀取
        if (DocumentExtensions.IsDocument(item.Path))
            return Missing(item, DocumentExtensions.ReadByToolNote);

        var size = new FileInfo(item.Path).Length;
        if (size > options.EmbedMaxFileBytes)
            return Missing(item, $"太大（{TextFiles.FormatSize(size)}），沒有放進訊息；可以用 read_file 分段讀取");
        if (embeddedBytes + size > options.EmbedMaxTotalBytes)
            return Missing(item, "這則訊息帶的檔案內容已達上限，沒有放進訊息；可以用 read_file 讀取");

        using var reader = TextFiles.Open(item.Path, out var error);
        if (reader is null)
            return Missing(item, $"{error}因此沒有放進訊息。");

        string text;
        try
        {
            text = reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            return Missing(item, $"{TextFiles.InvalidUtf8Message}因此沒有放進訊息。");
        }

        embeddedBytes += size;
        return new ContextBlock(ContextKind.File, item.DisplayName, item.Path, text, true);
    }

    private ContentBlock BuildFolder(ContextItem item, CancellationToken ct)
    {
        if (!Directory.Exists(item.Path))
            return Missing(item, "找不到這個資料夾（可能已被移動或刪除）");

        var tree = FolderTree.Build(item.Path, options.TreeMaxEntries, options.TreeMaxDepth, ct);
        return new ContextBlock(ContextKind.Folder, item.DisplayName, item.Path, tree.Text, true, null, tree.Entries);
    }

    /// <summary>
    /// 排程紀錄的引用卡（M24 4.7.3）：排程名稱、執行時間、最後一則助理回答的文字。紀錄或對話已被清掉、或是錯過（沒有對話）時，
    /// 用 <c>Note</c> 說明原因、<c>Embedded=false</c>，和其他讀不到的項目一樣。內嵌的區塊把執行時間放在 <c>Note</c>（畫面的引用卡顯示用，送給模型時不用）。
    /// </summary>
    private async Task<ContentBlock> BuildScheduledRunAsync(ContextItem item, CancellationToken ct)
    {
        const string gone = "原紀錄已清除（排程紀錄或它的對話已經不在），沒有放進訊息。";
        if (runs is null || conversations is null)
            return ScheduledRunMissing(item, gone);

        var run = await runs.GetAsync(item.Path, ct).ConfigureAwait(false);
        if (run is null)
            return ScheduledRunMissing(item, gone);
        if (run.ConversationId is not { } conversationId)
            return ScheduledRunMissing(item, "這次排程沒有執行（沒有回答可以引用），沒有放進訊息。");
        if (await conversations.GetConversationAsync(conversationId, ct).ConfigureAwait(false) is null)
            return ScheduledRunMissing(item, gone);

        var messages = await conversations.GetMessagesAsync(conversationId, ct).ConfigureAwait(false);
        var answer = messages.LastOrDefault(m => m.Role == ChatRole.Assistant && m.Content.OfType<TextBlock>().Any(t => t.Text.Length > 0));
        var answerText = answer is null
            ? "（這次執行沒有文字回答）"
            : string.Concat(answer.Content.OfType<TextBlock>().Select(t => t.Text));

        var time = ScheduledRunContext.FormatTime(run.StartedAt ?? run.PlannedAt);
        var content = $"排程名稱：{item.DisplayName}\n執行時間：{time}\n\n{answerText}";
        return new ContextBlock(ContextKind.ScheduledRun, item.DisplayName, item.Path, content, true, time);
    }

    private static ContextBlock ScheduledRunMissing(ContextItem item, string note) =>
        new(ContextKind.ScheduledRun, item.DisplayName, item.Path, null, false, note);

    private async Task<ContentBlock> BuildImageAsync(ContextItem item, bool save, CancellationToken ct)
    {
        if (!File.Exists(item.Path))
            return Missing(item, "找不到這個圖片檔案（可能已被移動或刪除）");

        var size = new FileInfo(item.Path).Length;
        if (size > ImageFormats.MaxBytes)
            return Missing(item, $"圖片太大（{TextFiles.FormatSize(size)}，上限 {TextFiles.FormatSize(ImageFormats.MaxBytes)}）");

        var bytes = await File.ReadAllBytesAsync(item.Path, ct).ConfigureAwait(false);
        var mediaType = ImageFormats.Detect(bytes);
        if (mediaType is null)
            return Missing(item, "不支援的圖片格式（支援 PNG、JPEG、GIF、WebP）");

        // 只在存成附件時縮（預估 token 的路徑不縮）；縮不了（GIF、解不開、已經夠小）回 null，就存原圖
        if (save && downscaler?.Downscale(bytes, mediaType, MaxImageEdge) is { } smaller)
        {
            bytes = smaller.Data;
            mediaType = smaller.MediaType;
        }

        var fileName = save ? await attachments.SaveAsync(bytes, mediaType, ct).ConfigureAwait(false) : "";
        return new ImageBlock(fileName, mediaType, item.DisplayName);
    }
}
