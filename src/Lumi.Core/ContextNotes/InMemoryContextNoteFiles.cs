using System.IO.Compression;
using System.Text;
using Lumi.Core.Context;

namespace Lumi.Core.ContextNotes;

/// <summary>
/// <see cref="IContextNoteFiles"/> 的記憶體版本，給測試與預設用。規則與 <c>FileContextNoteFiles</c> 一致
/// （圖片依內容判斷格式、大小上限、<see cref="ResolveImagePath"/> 只接受 <c>images/檔名</c>）。
/// <see cref="ResolveImagePath"/> 回的是不存在於磁碟的虛擬路徑；<see cref="ExportZipAsync"/> 真的寫出 zip。
/// </summary>
public sealed class InMemoryContextNoteFiles : IContextNoteFiles
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _notes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, byte[]>> _images = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryContextNoteFiles(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>虛擬的資料夾根目錄。</summary>
    public const string Root = "memory-context-notes";

    public string GetFolder(string noteId) => Path.Combine(Root, noteId);

    public Task<string?> ReadNoteAsync(string noteId, CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_notes.GetValueOrDefault(noteId));
    }

    public Task WriteNoteAsync(string noteId, string fileText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileText);
        lock (_lock)
            _notes[noteId] = fileText;
        return Task.CompletedTask;
    }

    /// <summary>測試用：模擬在其他程式改了 note.md（null＝刪掉檔案）。</summary>
    public void SetNoteText(string noteId, string? fileText)
    {
        lock (_lock)
        {
            if (fileText is null)
                _notes.Remove(noteId);
            else
                _notes[noteId] = fileText;
        }
    }

    public Task<string> SaveImageAsync(string noteId, ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
    {
        if (data.Length > ImageFormats.MaxBytes)
            throw new ArgumentException("圖片太大。", nameof(data));
        var detected = ImageFormats.Detect(data.Span) ?? throw new ArgumentException("不支援的圖片格式。", nameof(data));
        var extension = ImageFormats.Extension(detected) ?? throw new ArgumentException("不支援的圖片格式。", nameof(data));
        var stamp = _time.GetUtcNow().ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (!_images.TryGetValue(noteId, out var images))
                _images[noteId] = images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var n = 1;
            string name;
            while (images.ContainsKey(name = $"{stamp}-{n}.{extension}"))
                n++;
            images[name] = data.ToArray();
            return Task.FromResult("images/" + name);
        }
    }

    public string? ResolveImagePath(string noteId, string relativePath)
    {
        var name = ImageName(relativePath);
        if (name is null)
            return null;
        lock (_lock)
            return _images.TryGetValue(noteId, out var images) && images.ContainsKey(name) ? Path.Combine(GetFolder(noteId), "images", name) : null;
    }

    /// <summary>測試用：讀回存進去的圖片（沒有回 null）。</summary>
    public byte[]? GetImage(string noteId, string relativePath)
    {
        var name = ImageName(relativePath);
        lock (_lock)
            return name is not null && _images.TryGetValue(noteId, out var images) ? images.GetValueOrDefault(name) : null;
    }

    public Task DeleteAsync(string noteId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _notes.Remove(noteId);
            _images.Remove(noteId);
        }
        return Task.CompletedTask;
    }

    public Task ExportZipAsync(string noteId, string destinationZipPath, CancellationToken ct = default)
    {
        string? note;
        KeyValuePair<string, byte[]>[] images;
        lock (_lock)
        {
            note = _notes.GetValueOrDefault(noteId);
            images = _images.TryGetValue(noteId, out var map) ? map.ToArray() : [];
        }
        if (File.Exists(destinationZipPath))
            File.Delete(destinationZipPath);
        using var zip = ZipFile.Open(destinationZipPath, ZipArchiveMode.Create);
        using (var stream = zip.CreateEntry("note.md").Open())
            stream.Write(Encoding.UTF8.GetBytes(note ?? ""));
        foreach (var (name, bytes) in images)
        {
            using var stream = zip.CreateEntry("images/" + name).Open();
            stream.Write(bytes);
        }
        return Task.CompletedTask;
    }

    private static string? ImageName(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || !relativePath.StartsWith("images/", StringComparison.Ordinal))
            return null;
        var name = relativePath["images/".Length..];
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
            return null;
        return name;
    }
}
