using System.Text.Json;
using Lumi.Core.Updates;
using Lumi.Core.Web;

namespace Lumi.Web.Updates;

/// <summary>
/// 讀 GitHub Releases 的最新正式版本（<c>releases/latest</c> 不含草稿與預覽版）。走 <see cref="IWebClient"/>，所以和其他網路請求一樣經過使用者設定的 Proxy。
/// 發佈說明直接用 Release 頁面上的內容；下載頁的網址一律由 repo 與 tag 組出來，不採信回應裡的網址。
/// </summary>
internal sealed class GitHubReleaseUpdateChecker(IWebClient web) : IUpdateChecker
{
    public const string Repository = "NEILLOG/DesktopAgent";

    /// <summary>發佈說明最多保留的字數：只是給人看的摘要，超過的部分請使用者到網頁看。</summary>
    internal const int MaxNotesLength = 6000;

    private static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public async Task<UpdateInfo?> GetLatestAsync(CancellationToken ct)
    {
        var request = new WebRequest(LatestUrl)
        {
            Headers = new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json" },
            MaxBytes = 512 * 1024,
        };

        WebResponse response;
        try
        {
            response = await web.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (WebRequestException ex)
        {
            throw new UpdateCheckException(ex.Message, ex);
        }

        if (response.StatusCode == 404)
            return null;   // 還沒有任何正式版本
        if (response.StatusCode is 403 or 429)
            throw new UpdateCheckException("更新伺服器暫時不接受查詢，請稍後再試。");
        if (response.StatusCode is < 200 or >= 300)
            throw new UpdateCheckException($"更新伺服器回應 {response.StatusCode}。");

        return Parse(response.Body);
    }

    internal static UpdateInfo? Parse(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new UpdateCheckException("更新伺服器的回應格式不正確。");

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True
                || root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
                return null;

            var tag = Text(root, "tag_name");
            if (!AppVersion.TryParse(tag, out _))
                throw new UpdateCheckException("更新伺服器的回應格式不正確。");

            var version = AppVersion.Normalize(tag);
            var title = Text(root, "name");
            var notes = Text(root, "body").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (notes.Length > MaxNotesLength)
                notes = notes[..MaxNotesLength].TrimEnd() + "…";

            DateTimeOffset? published = DateTimeOffset.TryParse(Text(root, "published_at"), out var at) ? at : null;
            var page = new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag.Trim())}");
            return new UpdateInfo(version, title.Length > 0 ? title : $"Lumi {version}", notes, page, published);
        }
        catch (JsonException ex)
        {
            throw new UpdateCheckException("更新伺服器的回應格式不正確。", ex);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
