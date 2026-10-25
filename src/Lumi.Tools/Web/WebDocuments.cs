using Lumi.Core.Documents;
using Lumi.Core.Web;

namespace Lumi.Tools.Web;

/// <summary>
/// 網頁抓到的文件（PDF、Office）：先寫到暫存檔，交給 <see cref="IDocumentReader"/> 讀，讀完刪除。
/// 這是 Lumi 自己的暫存檔，不是使用者的檔案，所以不經過範圍（檔名不是 *Tool.cs，架構測試不檢查）。
/// </summary>
internal static class WebDocuments
{
    public static async Task<DocumentReadResult> ReadAsync(IDocumentReader reader, byte[] body, string extension, int? offset, int? maxChars, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "lumi-web-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await File.WriteAllBytesAsync(path, body, ct);
            var request = new DocumentReadRequest(
                path,
                Offset: Math.Max(0, offset ?? 0),
                MaxChars: Math.Clamp(maxChars ?? WebLimits.DefaultMaxChars, WebLimits.MinMaxChars, WebLimits.MaxChars));
            return await reader.ReadAsync(request, ct);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 刪除失敗就算了，暫存檔留在系統的暫存資料夾。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
