using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Lumi.Documents.Tests.Samples;

/// <summary>
/// 產生 .docx 樣本（程式組出來，不提交二進位檔）：標題（Heading1／2）、一般段落、項目符號清單、2×3 表格、中文與 emoji。
/// 測試用的文字常數放在這裡，斷言時直接引用。
/// </summary>
public static class DocxBuilder
{
    public const string Title = "Lumi 文件讀取測試";
    public const string SubTitle = "1.1 表格與清單";
    public const string Paragraph = "這是第一段中文內容，用來確認 UTF-8 與標點（，。！）能正確讀出。";
    public const string Bullet1 = "第一項：清單中文";
    public const string Bullet2 = "第二項：emoji 😀 測試";
    public const string Closing = "最後一段。";

    /// <summary>表格的內容：2 列 × 3 欄（第一列是表頭）。</summary>
    public static readonly string[][] TableRows =
    [
        ["項目", "數量", "備註"],
        ["蘋果", "3", "紅色"],
    ];

    public static byte[] CreateBytes()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = doc.AddMainDocumentPart();
            AddStyles(main);
            AddNumbering(main);

            main.Document = new Document(new Body(
                Heading(Title, "Heading1"),
                Plain(Paragraph),
                Heading(SubTitle, "Heading2"),
                Bullet(Bullet1),
                Bullet(Bullet2),
                Plain("以下是 2×3 表格："),
                BuildTable(),
                Heading("1.2 結尾", "Heading2"),
                Plain(Closing)));
        }

        return stream.ToArray();
    }

    /// <summary>寫入 <paramref name="directory"/>，回傳完整路徑。</summary>
    public static string Create(string directory, string fileName = "sample.docx") =>
        SampleFiles.Write(directory, fileName, CreateBytes());

    private static Paragraph Heading(string text, string styleId) => new(
        new ParagraphProperties(new ParagraphStyleId { Val = styleId }),
        Run(text));

    private static Paragraph Plain(string text) => new(Run(text));

    private static Paragraph Bullet(string text) => new(
        new ParagraphProperties(new NumberingProperties(
            new NumberingLevelReference { Val = 0 },
            new NumberingId { Val = 1 })),
        Run(text));

    private static Run Run(string text) => new(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

    private static Table BuildTable()
    {
        var table = new Table(
            new TableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })),
            new TableGrid(new GridColumn(), new GridColumn(), new GridColumn()));

        foreach (var row in TableRows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row)
                tableRow.Append(new TableCell(new Paragraph(Run(cell))));
            table.Append(tableRow);
        }
        return table;
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            HeadingStyle("Heading1", "heading 1", outlineLevel: 0, halfPoints: 32),
            HeadingStyle("Heading2", "heading 2", outlineLevel: 1, halfPoints: 26));
    }

    private static Style HeadingStyle(string styleId, string name, int outlineLevel, int halfPoints) => new(
        new StyleName { Val = name },
        new StyleParagraphProperties(new OutlineLevel { Val = outlineLevel }),
        new StyleRunProperties(new Bold(), new FontSize { Val = halfPoints.ToString() }))
    {
        Type = StyleValues.Paragraph,
        StyleId = styleId,
    };

    private static void AddNumbering(MainDocumentPart main)
    {
        var numbering = main.AddNewPart<NumberingDefinitionsPart>();
        numbering.Numbering = new Numbering(
            new AbstractNum(
                new MultiLevelType { Val = MultiLevelValues.HybridMultilevel },
                new Level(
                    new StartNumberingValue { Val = 1 },
                    new NumberingFormat { Val = NumberFormatValues.Bullet },
                    new LevelText { Val = "•" },
                    new LevelJustification { Val = LevelJustificationValues.Left })
                { LevelIndex = 0 })
            { AbstractNumberId = 0 },
            new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
    }
}
