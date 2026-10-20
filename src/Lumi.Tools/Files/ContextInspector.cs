using Lumi.Core.Context;

namespace Lumi.Tools;

/// <summary><see cref="IContextInspector"/> 的實作：和快照建構器用同一組上限，所以 chip 上標的問題就是送出時會發生的事。</summary>
public sealed class ContextInspector(FileToolOptions options) : IContextInspector
{
    /// <summary>資料夾項目數數到這個數量就停（chip 顯示「10,000+」）。</summary>
    public const int EntryCountCap = 10_000;

    public Task<ContextKind?> ClassifyAsync(string path, CancellationToken ct = default) =>
        Task.Run<ContextKind?>(() =>
        {
            try
            {
                if (Directory.Exists(path))
                    return ContextKind.Folder;
                if (!File.Exists(path))
                    return null;

                var head = new byte[16];
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var read = stream.Read(head, 0, head.Length);
                return ImageFormats.Detect(head.AsSpan(0, read)) is not null ? ContextKind.Image : ContextKind.File;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 讀不到內容（權限、占用）：當成一般檔案，狀態會再說明
                return File.Exists(path) ? ContextKind.File : null;
            }
        }, ct);

    public Task<ContextItemStatus> InspectAsync(ContextKind kind, string path, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                return kind switch
                {
                    ContextKind.Folder => InspectFolder(path, ct),
                    ContextKind.Image => InspectImage(path),
                    _ => InspectFile(path),
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new ContextItemStatus(ContextItemProblem.NotText);
            }
        }, ct);

    private ContextItemStatus InspectFile(string path)
    {
        if (!File.Exists(path))
            return new ContextItemStatus(Directory.Exists(path) ? ContextItemProblem.NotText : ContextItemProblem.Missing);

        var size = new FileInfo(path).Length;
        using var reader = TextFiles.Open(path, out _);
        if (reader is null)
            return new ContextItemStatus(ContextItemProblem.NotText, size);

        return new ContextItemStatus(size > options.EmbedMaxFileBytes ? ContextItemProblem.TooLarge : ContextItemProblem.None, size);
    }

    private static ContextItemStatus InspectFolder(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path))
            return new ContextItemStatus(ContextItemProblem.Missing);
        var (count, capped) = FolderTree.Count(path, EntryCountCap, ct);
        return new ContextItemStatus(ContextItemProblem.None, null, count, capped);
    }

    private static ContextItemStatus InspectImage(string path)
    {
        if (!File.Exists(path))
            return new ContextItemStatus(ContextItemProblem.Missing);

        var size = new FileInfo(path).Length;
        if (size > ImageFormats.MaxBytes)
            return new ContextItemStatus(ContextItemProblem.ImageTooLarge, size);

        var head = new byte[16];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var read = stream.Read(head, 0, head.Length);
        return ImageFormats.Detect(head.AsSpan(0, read)) is null
            ? new ContextItemStatus(ContextItemProblem.UnsupportedImage, size)
            : new ContextItemStatus(ContextItemProblem.None, size);
    }
}
