using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Lumi.Documents.Tests.Samples;

/// <summary>
/// 產生 3 頁 PDF（PdfPig 的 <see cref="PdfDocumentBuilder"/>）：第 1 頁中文、第 2 頁英文、第 3 頁完全空白（模擬掃描頁）。
/// 中文用打包的 Noto Sans TC（測試專案把它複製到輸出資料夾的 Fonts/ 底下）。
/// </summary>
public static class PdfBuilder
{
    public const string ChinesePage = "中文測試頁：Lumi 文件讀取";
    public const string EnglishPage = "English page: Lumi document reader.";

    /// <summary>字型檔的位置（測試輸出資料夾裡的 Fonts/NotoSansTC-Regular.ttf）。</summary>
    public static string FontPath { get; } = Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSansTC-Regular.ttf");

    public static byte[] CreateBytes()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddTrueTypeFont(File.ReadAllBytes(FontPath));

        var chinese = builder.AddPage(PageSize.A4);
        chinese.AddText(ChinesePage, 16, new PdfPoint(50, 780), font);

        var english = builder.AddPage(PageSize.A4);
        english.AddText(EnglishPage, 16, new PdfPoint(50, 780), font);

        builder.AddPage(PageSize.A4);   // 空白頁：模擬掃描頁（沒有可擷取的文字）

        return builder.Build();
    }

    /// <summary>寫入 <paramref name="directory"/>，回傳完整路徑。</summary>
    public static string Create(string directory, string fileName = "sample.pdf") =>
        SampleFiles.Write(directory, fileName, CreateBytes());
}
