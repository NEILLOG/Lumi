using System.IO.Compression;
using System.Globalization;
using System.Text;
using Lumi.Core.Context;
using Lumi.Core.ContextNotes;

namespace Lumi.Infrastructure.ContextNotes;

/// <summary>
/// <see cref="IContextNoteFiles"/> 的檔案版本（M35）：一本研究本一個資料夾 <c>{根目錄}/{研究本 id}/</c>，裡面是 <c>note.md</c> 與 <c>images/</c>。
/// 研究本 id 一律是 GUID（它會變成資料夾名稱），其他字串丟 <see cref="ArgumentException"/>。
/// </summary>
public sealed class FileContextNoteFiles(ContextNoteFileOptions options, TimeProvider time) : IContextNoteFiles
{
    private const string NoteFileName = "note.md";
    private const string ImagesFolder = "images";
    private const string ImagePrefix = ImagesFolder + "/";

    public string GetFolder(string noteId) => Path.Combine(options.ResolveDirectory(), CheckedId(noteId));

    public async Task<string?> ReadNoteAsync(string noteId, CancellationToken ct = default)
    {
        var path = Path.Combine(GetFolder(noteId), NoteFileName);
        if (!File.Exists(path))
            return null;
        return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public async Task WriteNoteAsync(string noteId, string fileText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileText);
        var folder = GetFolder(noteId);
        Directory.CreateDirectory(folder);

        // 先寫暫存檔，再整個取代，避免寫到一半的檔案被讀到
        var target = Path.Combine(folder, NoteFileName);
        var temp = Path.Combine(folder, $".note-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, fileText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<string> SaveImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
    {
        // 不信 mediaType 參數，格式一律依內容判斷（同 ImageFormats.Detect）
        if (data.Length > ImageFormats.MaxBytes)
            throw new ArgumentException("圖片太大。", nameof(data));
        var detected = ImageFormats.Detect(data.Span) ?? throw new ArgumentException("不支援的圖片格式。", nameof(data));
        var extension = ImageFormats.Extension(detected) ?? throw new ArgumentException("不支援的圖片格式。", nameof(data));

        var imagesFolder = Path.Combine(GetFolder(noteId), ImagesFolder);
        Directory.CreateDirectory(imagesFolder);

        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var name = $"{stamp}-{n}.{extension}";
            var path = Path.Combine(imagesFolder, name);
            if (File.Exists(path))
                continue;
            try
            {
                // CreateNew：同時間另一個呼叫搶先建立時會失敗，改試下一個序號
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                return ImagePrefix + name;
            }
            catch (IOException) when (File.Exists(path))
            {
                // 檔案已存在，換下一個序號
            }
        }
    }

    public string? ResolveImagePath(string noteId, string relativePath)
    {
        var folder = GetFolder(noteId);
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath) || !relativePath.StartsWith(ImagePrefix, StringComparison.Ordinal))
            return null;

        var name = relativePath[ImagePrefix.Length..];
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Contains("..", StringComparison.Ordinal))
            return null;

        var full = Path.Combine(folder, ImagesFolder, name);
        return File.Exists(full) ? full : null;
    }

    public Task DeleteAsync(string noteId, CancellationToken ct = default)
    {
        var folder = GetFolder(noteId);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        return Task.CompletedTask;
    }

    public Task ExportZipAsync(string noteId, string destinationZipPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationZipPath);
        var folder = GetFolder(noteId);
        if (File.Exists(destinationZipPath))
            File.Delete(destinationZipPath);

        using var zip = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
        var notePath = Path.Combine(folder, NoteFileName);
        if (File.Exists(notePath))
            zip.CreateEntryFromFile(notePath, NoteFileName);

        var imagesFolder = Path.Combine(folder, ImagesFolder);
        if (Directory.Exists(imagesFolder))
        {
            foreach (var path in Directory.EnumerateFiles(imagesFolder).Order(StringComparer.Ordinal))
                zip.CreateEntryFromFile(path, ImagePrefix + Path.GetFileName(path));
        }
        return Task.CompletedTask;
    }

    /// <summary>研究本 id 必須是 GUID，因為它會變成資料夾名稱。</summary>
    private static string CheckedId(string noteId)
    {
        if (!Guid.TryParse(noteId, out _))
            throw new ArgumentException("研究本 id 必須是 GUID。", nameof(noteId));
        return noteId;
    }
}
