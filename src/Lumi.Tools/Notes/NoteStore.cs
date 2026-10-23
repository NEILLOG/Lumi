using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lumi.Core.Notes;

namespace Lumi.Tools.Notes;

/// <summary>
/// <see cref="INoteStore"/> 的檔案實作（格式見 plan/09-convert-from-old.md 第 6 節）：每天一個 <c>yyyy-MM-dd.md</c>，
/// 新檔第一行 <c>{日期}</c> 標題，每則是 <c>- HH:mm [分類] 內容</c>，續行縮排兩格，UTF-8 無 BOM、LF，只追加。
/// 檔名一律由日期產生，呼叫端給的字串不會變成路徑；符號連結與捷徑不讀也不寫。
/// </summary>
internal sealed partial class NoteStore(INoteFolderProvider folders, NoteOptions options) : INoteStore
{
    // 同一程序內的追加共用一把鎖，避免兩筆同時追加時內容交錯（資料夾通常只有一個，鎖的範圍不是瓶頸）
    private static readonly SemaphoreSlim AppendLock = new(1, 1);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm";
    private const string ContinuationIndent = "  ";

    public string FolderPath => folders.Folder;

    public async Task AppendAsync(DateOnly date, TimeOnly time, string? category, string content, CancellationToken ct)
    {
        var folder = folders.Folder;
        var path = FilePath(folder, date);
        var entry = FormatEntry(time, category, content);

        await AppendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsLink(path))
                throw new NoteStoreException("筆記檔案是符號連結或捷徑，為了安全不寫入。");

            try
            {
                Directory.CreateDirectory(folder);
                await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite,
                    bufferSize: 4096, FileOptions.Asynchronous);

                var prefix = await LeadingTextAsync(stream, date, ct).ConfigureAwait(false);
                var bytes = Utf8NoBom.GetBytes(prefix + entry);
                // 讀取時超過上限的檔案會整個略過，所以寫入時就要擋下：寫得進去、讀不出來的筆記等於遺失
                if (stream.Length + bytes.Length > options.MaxFileBytes)
                    throw new NoteStoreException("這天的筆記已經太長（上限 256 KB），請改記到其他日期。");

                stream.Seek(0, SeekOrigin.End);
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new NoteStoreException("無法寫入筆記，請確認筆記資料夾可以寫入。");
            }
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public Task<IReadOnlyList<NoteDay>> ReadAsync(DateOnly from, DateOnly to, string? keyword, CancellationToken ct)
    {
        var folder = folders.Folder;
        if (to < from || !Directory.Exists(folder))
            return Task.FromResult<IReadOnlyList<NoteDay>>([]);

        var needle = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim();
        var files = DatedFiles(folder, from, to);

        var days = new List<NoteDay>();
        foreach (var (date, path) in files)
        {
            ct.ThrowIfCancellationRequested();
            var entries = ReadEntries(path, needle);
            if (entries.Count > 0)
                days.Add(new NoteDay(date, entries));
        }
        return Task.FromResult<IReadOnlyList<NoteDay>>(days);
    }

    /// <summary>資料夾裡符合 <c>yyyy-MM-dd.md</c> 且落在範圍內的檔案，依日期由舊到新。其他檔案（Obsidian 的設定等）完全不碰。</summary>
    private static List<(DateOnly Date, string Path)> DatedFiles(string folder, DateOnly from, DateOnly to)
    {
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFiles(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new NoteStoreException("無法讀取筆記資料夾，請確認資料夾可以讀取。");
        }

        var result = new List<(DateOnly, string)>();
        foreach (var path in names)
        {
            var match = NoteFileName().Match(Path.GetFileName(path));
            if (!match.Success
                || !DateOnly.TryParseExact(match.Groups[1].Value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date < from || date > to)
                continue;
            result.Add((date, path));
        }
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }

    /// <summary>
    /// 讀一個檔案的記事。檔案有問題（符號連結、太大、讀不到）時回空清單，不丟例外；
    /// 內容不合預期（亂碼、空檔、只有標題）也只是得到較少的記事。
    /// </summary>
    private List<NoteEntry> ReadEntries(string path, string? needle)
    {
        if (IsLink(path))
            return [];

        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > options.MaxFileBytes)
                return [];

            // 讀取期間檔案可能變大：最多讀上限加一個位元組，超過就整個略過
            var limit = options.MaxFileBytes + 1;
            var bytes = new byte[limit];
            var total = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, FileOptions.SequentialScan))
            {
                int n;
                while (total < limit && (n = stream.Read(bytes, total, (int)(limit - total))) > 0)
                    total += n;
            }
            if (total > options.MaxFileBytes)
                return [];
            // 解碼不丟例外（無效位元組換成替代字元）：亂碼的檔案只是讀到亂碼，不會讓整次讀取失敗
            text = Utf8NoBom.GetString(bytes, 0, total);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var entries = ParseEntries(text);
        if (needle is null)
            return entries;
        return entries
            .Where(e => e.Content.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || (e.Category?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();
    }

    /// <summary>
    /// 切成記事：第一個標題行（<c>#</c> 開頭）跳過；以 <c>- </c> 或 <c>* </c> 開頭的行開始一則；
    /// 第一個項目符號之前的文字（非空白）自成一則；縮排兩格的續行併入前一則。
    /// </summary>
    internal static List<NoteEntry> ParseEntries(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var start = 0;
        while (start < lines.Length && lines[start].Trim().Length == 0)
            start++;
        if (start < lines.Length && lines[start].StartsWith("# ", StringComparison.Ordinal))
            start++;

        var leading = new List<string>();
        var pending = new List<(TimeOnly? Time, string? Category, List<string> Lines)>();
        List<string>? current = null;
        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                var (time, category, first) = ParseBulletHead(line[2..]);
                current = [first];
                pending.Add((time, category, current));
            }
            else if (current is null)
            {
                leading.Add(line);
            }
            else
            {
                current.Add(line.StartsWith(ContinuationIndent, StringComparison.Ordinal) ? line[ContinuationIndent.Length..] : line);
            }
        }

        var entries = new List<NoteEntry>();
        var preamble = string.Join("\n", leading).Trim();
        if (preamble.Length > 0)
            entries.Add(new NoteEntry(null, null, preamble));

        foreach (var (time, category, body) in pending)
            entries.Add(new NoteEntry(time, category, string.Join("\n", body).Trim()));
        return entries;
    }

    /// <summary>項目符號後的第一段：<c>HH:mm [分類] 內容</c>。時間或分類不符合格式時整段當作內容。</summary>
    private static (TimeOnly? Time, string? Category, string Content) ParseBulletHead(string rest)
    {
        if (rest.Length < 5 || (rest.Length > 5 && rest[5] != ' ')
            || !TimeOnly.TryParseExact(rest[..5], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return (null, null, rest);

        var after = rest.Length > 6 ? rest[6..] : "";
        string? category = null;
        if (after.StartsWith('['))
        {
            var close = after.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                category = after[1..close].Trim();
                after = after[(close + 1)..];
                if (after.StartsWith(' '))
                    after = after[1..];
            }
        }
        return (time, string.IsNullOrEmpty(category) ? null : category, after);
    }

    /// <summary>
    /// 追加前要先寫的文字：新檔（或空檔）要寫標題行與空行；既有檔案末行沒有換行就先補一個。
    /// </summary>
    private static async Task<string> LeadingTextAsync(FileStream stream, DateOnly date, CancellationToken ct)
    {
        if (stream.Length == 0)
            return $"# {date.ToString(DateFormat, CultureInfo.InvariantCulture)}\n\n";

        stream.Seek(-1, SeekOrigin.End);
        var last = new byte[1];
        var read = await stream.ReadAsync(last.AsMemory(0, 1), ct).ConfigureAwait(false);
        return read == 1 && last[0] != (byte)'\n' ? "\n" : "";
    }

    /// <summary>一則記事的文字（含結尾換行）。續行縮排兩格；空白行保持空白，不加縮排。</summary>
    internal static string FormatEntry(TimeOnly time, string? category, string content)
    {
        var builder = new StringBuilder();
        builder.Append("- ").Append(time.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append(' ');
        if (!string.IsNullOrWhiteSpace(category))
            builder.Append('[').Append(category.Trim()).Append("] ");

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Split('\n');
        builder.Append(lines[0]);
        foreach (var line in lines.Skip(1))
        {
            builder.Append('\n');
            if (line.Length > 0)
                builder.Append(ContinuationIndent).Append(line);
        }
        return builder.Append('\n').ToString();
    }

    private static string FilePath(string folder, DateOnly date) =>
        Path.Combine(folder, date.ToString(DateFormat, CultureInfo.InvariantCulture) + ".md");

    /// <summary>符號連結或捷徑（Windows 的 reparse point）。不存在的檔案不算。</summary>
    private static bool IsLink(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            return true;
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2})\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex NoteFileName();
}
