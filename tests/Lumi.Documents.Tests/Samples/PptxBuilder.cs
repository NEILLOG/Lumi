using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;

namespace Lumi.Documents.Tests.Samples;

/// <summary>
/// 產生 .pptx 樣本：3 張投影片。第 1 張標題＋內文；第 2 張標題＋表格（2×2）；第 3 張標題＋內文＋講者備註。
/// 投影片、版面、母片與佈景主題用 XML 直接寫入（比 SDK 的型別樹短很多），沒有圖片。
/// </summary>
public static class PptxBuilder
{
    public const string Slide1Title = "Lumi 簡報測試";
    public const string Slide1Body = "第一張內文：中文與 English 混排";
    public const string Slide2Title = "進度表";
    public const string Slide3Title = "第三張";
    public const string Slide3Body = "第三張內文";
    public const string Slide3Notes = "講者備註：這裡是備註內容";

    /// <summary>第 2 張的表格，2 列 × 2 欄。</summary>
    public static readonly string[][] Slide2TableRows =
    [
        ["階段", "狀態"],
        ["M10", "進行中"],
    ];

    private const string PresentationNs = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string GroupHeader = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

    public static byte[] CreateBytes()
    {
        using var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation, autoSave: true))
        {
            var presentation = doc.AddPresentationPart();

            // 母片、版面、佈景主題（母片與版面互相引用，所以先建版面再寫母片）
            var master = presentation.AddNewPart<SlideMasterPart>();
            var layout = master.AddNewPart<SlideLayoutPart>();
            var theme = master.AddNewPart<ThemePart>();
            layout.FeedData(Xml(LayoutXml()));
            master.FeedData(Xml(MasterXml(master.GetIdOfPart(layout))));
            theme.FeedData(Xml(ThemeXml()));

            var slideIds = new List<(string Id, string Rel)>();
            for (var i = 0; i < 3; i++)
            {
                var slide = presentation.AddNewPart<SlidePart>();
                slide.AddPart(layout);
                slide.FeedData(Xml(SlideXml(i)));

                if (i == 2)
                {
                    var notes = slide.AddNewPart<NotesSlidePart>();
                    notes.AddPart(slide);
                    notes.FeedData(Xml(NotesXml(Slide3Notes)));
                }
                slideIds.Add(((256 + i).ToString(), presentation.GetIdOfPart(slide)));
            }

            presentation.FeedData(Xml(PresentationXml(presentation.GetIdOfPart(master), slideIds)));
        }

        return stream.ToArray();
    }

    /// <summary>寫入 <paramref name="directory"/>，回傳完整路徑。</summary>
    public static string Create(string directory, string fileName = "sample.pptx") =>
        SampleFiles.Write(directory, fileName, CreateBytes());

    private static MemoryStream Xml(string xml) => new(Encoding.UTF8.GetBytes(xml));

    private static string PresentationXml(string masterRel, List<(string Id, string Rel)> slides)
    {
        var sldIds = string.Concat(slides.Select(s => $"<p:sldId id=\"{s.Id}\" r:id=\"{s.Rel}\"/>"));
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<p:presentation {PresentationNs} saveSubsetFonts=\"1\">" +
               $"<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"{masterRel}\"/></p:sldMasterIdLst>" +
               $"<p:sldIdLst>{sldIds}</p:sldIdLst>" +
               "<p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>";
    }

    private static string MasterXml(string layoutRel) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        $"<p:sldMaster {PresentationNs}><p:cSld><p:spTree>{GroupHeader}</p:spTree></p:cSld>" +
        "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
        $"<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"{layoutRel}\"/></p:sldLayoutIdLst></p:sldMaster>";

    private static string LayoutXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        $"<p:sldLayout {PresentationNs} type=\"titleAndContent\" preserve=\"1\"><p:cSld name=\"標題及內容\"><p:spTree>{GroupHeader}" +
        Placeholder(2, "標題", "<p:ph type=\"title\"/>", 0, 0, 12192000, 1100000) +
        Placeholder(3, "內容", "<p:ph idx=\"1\"/>", 0, 1300000, 12192000, 4800000) +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>";

    private static string SlideXml(int index)
    {
        var shapes = index switch
        {
            0 => Placeholder(2, "標題", "<p:ph type=\"title\"/>", 0, 0, 12192000, 1100000, Slide1Title) +
                 Placeholder(3, "內容", "<p:ph idx=\"1\"/>", 0, 1300000, 12192000, 4800000, Slide1Body),
            1 => Placeholder(2, "標題", "<p:ph type=\"title\"/>", 0, 0, 12192000, 1100000, Slide2Title) + Table(),
            _ => Placeholder(2, "標題", "<p:ph type=\"title\"/>", 0, 0, 12192000, 1100000, Slide3Title) +
                 Placeholder(3, "內容", "<p:ph idx=\"1\"/>", 0, 1300000, 12192000, 4800000, Slide3Body),
        };

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
               $"<p:sld {PresentationNs}><p:cSld><p:spTree>{GroupHeader}{shapes}</p:spTree></p:cSld>" +
               "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
    }

    private static string Placeholder(int id, string name, string ph, long x, long y, long cx, long cy, string? text = null)
    {
        var body = text is null
            ? "<a:p><a:endParaRPr lang=\"zh-TW\"/></a:p>"
            : $"<a:p><a:r><a:rPr lang=\"zh-TW\"/><a:t>{Escape(text)}</a:t></a:r></a:p>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr>" +
               $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm></p:spPr>" +
               $"<p:txBody><a:bodyPr/><a:lstStyle/>{body}</p:txBody></p:sp>";
    }

    private static string Table()
    {
        var rows = string.Concat(Slide2TableRows.Select(row =>
            "<a:tr h=\"370840\">" +
            string.Concat(row.Select(cell => $"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"zh-TW\"/><a:t>{Escape(cell)}</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>")) +
            "</a:tr>"));
        return "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"4\" name=\"表格\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>" +
               "<p:xfrm><a:off x=\"838200\" y=\"1400000\"/><a:ext cx=\"6000000\" cy=\"740000\"/></p:xfrm>" +
               "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\"/>" +
               "<a:tblGrid><a:gridCol w=\"3000000\"/><a:gridCol w=\"3000000\"/></a:tblGrid>" + rows +
               "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
    }

    private static string NotesXml(string text) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        $"<p:notes {PresentationNs}><p:cSld><p:spTree>{GroupHeader}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"備註\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"zh-TW\"/><a:t>{Escape(text)}</a:t></a:r></a:p></p:txBody></p:sp>" +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>";

    private static string ThemeXml()
    {
        var accents = string.Concat(Enumerable.Range(1, 6).Select(i => $"<a:accent{i}><a:srgbClr val=\"{(i * 0x1F3A5C) % 0xFFFFFF:X6}\"/></a:accent{i}>"));
        var fillStyle = "<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>";
        var lineStyle = "<a:ln w=\"9525\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>";
        var effect = "<a:effectStyle><a:effectLst/></a:effectStyle>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
               "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Lumi 測試佈景\"><a:themeElements>" +
               "<a:clrScheme name=\"Lumi\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
               "<a:dk2><a:srgbClr val=\"1F2937\"/></a:dk2><a:lt2><a:srgbClr val=\"F3F4F6\"/></a:lt2>" + accents +
               "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
               "<a:fontScheme name=\"Lumi\"><a:majorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
               "<a:minorFont><a:latin typeface=\"Calibri\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
               "<a:fmtScheme name=\"Lumi\"><a:fillStyleLst>" + string.Concat(Enumerable.Repeat(fillStyle, 3)) + "</a:fillStyleLst>" +
               "<a:lnStyleLst>" + string.Concat(Enumerable.Repeat(lineStyle, 3)) + "</a:lnStyleLst>" +
               "<a:effectStyleLst>" + string.Concat(Enumerable.Repeat(effect, 3)) + "</a:effectStyleLst>" +
               "<a:bgFillStyleLst>" + string.Concat(Enumerable.Repeat(fillStyle, 3)) + "</a:bgFillStyleLst></a:fmtScheme>" +
               "</a:themeElements></a:theme>";
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
