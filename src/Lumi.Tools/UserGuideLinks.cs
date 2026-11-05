using Lumi.Core.Updates;

namespace Lumi.Tools;

/// <summary>使用說明的 GitHub 線上版網址，固定到目前執行版本的 tag，內容和打包進程式的版本一致。</summary>
public static class UserGuideLinks
{
    private const string Prefix = "https://github.com/NEILLOG/Lumi/blob/v";

    /// <summary>某一篇說明；不給代號時是目錄（<c>README.md</c>）。</summary>
    public static Uri For(string? topicId = null) =>
        new($"{Prefix}{AppVersion.Current}/docs/guide/{(string.IsNullOrEmpty(topicId) ? "README" : topicId)}.md");
}
