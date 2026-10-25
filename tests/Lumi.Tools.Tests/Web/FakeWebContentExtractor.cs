using System.Text;
using Lumi.Core.Web;

namespace Lumi.Tools.Tests.Web;

/// <summary>記錄呼叫的參數，回傳預先設定的頁面。不解析真的 HTML。</summary>
internal sealed class FakeWebContentExtractor : IWebContentExtractor
{
    public WebPage Page { get; set; } = new("假標題", "假內容", [], true);

    public List<bool> ReadableArgs { get; } = [];

    public List<string?> DecodeCharsets { get; } = [];

    public List<bool> DecodeIsHtml { get; } = [];

    public WebPage Extract(string html, Uri baseUrl, bool readable)
    {
        ReadableArgs.Add(readable);
        return Page;
    }

    public string DecodeText(byte[] body, string? charset, bool isHtml)
    {
        DecodeCharsets.Add(charset);
        DecodeIsHtml.Add(isHtml);
        return Encoding.UTF8.GetString(body);
    }
}
