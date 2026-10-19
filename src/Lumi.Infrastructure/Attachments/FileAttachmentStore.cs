using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lumi.Core.Context;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Attachments;

/// <summary>
/// 附件（圖片）的檔案存放：<c>attachments/&lt;sha256&gt;.&lt;ext&gt;</c>，相同內容只存一份。
/// 讀取時檔名必須完全符合這個格式，所以就算資料庫裡的檔名被改成 <c>../x</c> 也跳不出附件資料夾。
/// </summary>
public sealed partial class FileAttachmentStore(IOptions<AttachmentOptions> options) : IAttachmentStore
{
    [GeneratedRegex(@"^[0-9a-f]{64}\.(png|jpg|gif|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();

    private const string TempPrefix = ".tmp-";

    private readonly string _directory = options.Value.ResolveDirectory();

    public async Task<string> SaveAsync(ReadOnlyMemory<byte> data, string mediaType, CancellationToken ct = default)
    {
        var extension = ImageFormats.Extension(mediaType)
                        ?? throw new ArgumentException($"不支援的媒體類型：{mediaType}", nameof(mediaType));
        var name = $"{Convert.ToHexStringLower(SHA256.HashData(data.Span))}.{extension}";
        var path = Path.Combine(_directory, name);
        if (File.Exists(path))
            return name;

        Directory.CreateDirectory(_directory);
        // 先寫暫存檔再改名：寫到一半程式結束不會留下內容不完整、但檔名看起來正常的檔案
        var temp = Path.Combine(_directory, TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(temp, data.ToArray(), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);   // 兩邊同時存相同內容時，內容一樣，覆蓋沒有差別
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
        return name;
    }

    public async Task<byte[]?> ReadAsync(string fileName, CancellationToken ct = default)
    {
        if (!ValidName().IsMatch(fileName))
            return null;
        try
        {
            return await File.ReadAllBytesAsync(Path.Combine(_directory, fileName), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referencedFileNames, CancellationToken ct = default)
    {
        if (!Directory.Exists(_directory))
            return Task.FromResult(0);

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_directory))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            // 只動自己建立的檔案（符合命名格式的附件與殘留的暫存檔）；資料夾裡別的東西不碰
            var ours = ValidName().IsMatch(name) || name.StartsWith(TempPrefix, StringComparison.Ordinal);
            if (!ours || referencedFileNames.Contains(name))
                continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 被占用的下次再清
            }
        }
        return Task.FromResult(deleted);
    }
}
