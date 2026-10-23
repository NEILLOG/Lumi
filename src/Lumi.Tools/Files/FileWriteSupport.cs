using Lumi.Core.Tools;

namespace Lumi.Tools;

/// <summary>寫入類檔案工具共用：顯示路徑、預覽的保護、複製與跨磁碟區搬移。</summary>
internal static class FileWriteSupport
{
    /// <summary>複製用的緩衝區（舊版 1 MiB）。</summary>
    public const int BufferSize = 1024 * 1024;

    /// <summary>確認卡片的說明：檔案超過這個大小就只顯示大小，不數行（數行要整份讀，太慢）。</summary>
    public const long DetailSizeLimit = 1024 * 1024;

    /// <summary>範圍內的顯示路徑；解析失敗（範圍外、機密）時就用使用者給的字串。</summary>
    public static string Display(IFileScope scope, string raw)
    {
        var resolved = scope.Resolve(raw ?? "");
        return resolved.IsAllowed ? scope.ToDisplayPath(resolved.FullPath!) : (raw ?? "").Trim();
    }

    /// <summary>Describe 不可丟例外：任何失敗都退回只有使用者給的字串、沒有說明。</summary>
    public static ToolPreview SafePreview(Func<ToolPreview> build, string? fallbackTarget)
    {
        try
        {
            return build();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ToolPreview(fallbackTarget, null);
        }
    }

    /// <summary>Describe 的說明同樣不能丟例外；讀不到就是 null。</summary>
    public static string? SafeDetail(Func<string?> build)
    {
        try
        {
            return build();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>兩個完整路徑是不是同一個（依作業系統決定大小寫；Mac 與 Windows 不分大小寫）。</summary>
    public static bool SameFullPath(string a, string b) =>
        string.Equals(a, b, FileScopeRules.ForCurrentOs.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// 複製成一個新檔：目的以 CreateNew 建立（已存在就丟 IOException，不覆蓋）。
    /// 複製中途失敗或被取消時，刪掉我們建的半成品目的檔。
    /// </summary>
    public static async Task CopyNewAsync(string source, string destination, CancellationToken ct)
    {
        // 目的檔在這之後才算「我們建的」；CreateNew 失敗（已存在）時不能刪別人的檔案
        var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        var completed = false;
        try
        {
            await using (output)
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, useAsync: true))
            {
                await input.CopyToAsync(output, BufferSize, ct).ConfigureAwait(false);
            }
            completed = true;
        }
        finally
        {
            if (!completed)
                TextFileWriter.TryDelete(destination);
        }
    }

    /// <summary>
    /// 跨磁碟區的搬移：複製、比對大小、再刪來源。大小不符時還原目的檔、來源保留；刪來源失敗時也清掉目的檔。
    /// 失敗都丟 <see cref="IOException"/>，由工具轉成繁中說明。
    /// </summary>
    public static async Task MoveAcrossVolumesAsync(string source, string destination, CancellationToken ct)
    {
        await CopyNewAsync(source, destination, ct).ConfigureAwait(false);

        var sourceLength = new FileInfo(source).Length;
        if (new FileInfo(destination).Length != sourceLength)
        {
            TextFileWriter.TryDelete(destination);
            throw new IOException("檔案大小不符，已還原目的檔。");
        }

        try
        {
            File.Delete(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TextFileWriter.TryDelete(destination);
            throw new IOException("無法刪除來源檔案。", e);
        }
    }

    public static string Size(long bytes) => TextFiles.FormatSize(bytes);
}
