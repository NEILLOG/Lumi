#nullable disable   // OpenXml 的導覽屬性標成可為 null；測試直接取值，失敗時以 NullReferenceException 呈現
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Lumi.Core.Documents;
using Lumi.Documents.Tests.Samples;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Documents.Tests;

public class WordDocumentReaderTests
{
    private const string Empty = "[概覽] 段落 0、表格 0、標題 0\n---\n（這份文件沒有文字內容）";

    [Fact]
    public void 只接受docx副檔名不分大小寫()
    {
        var reader = new WordDocumentReader();

        Assert.True(reader.Supports(".docx"));
        Assert.True(reader.Supports(".DOCX"));
        Assert.False(reader.Supports(".docm"));
        Assert.False(reader.Supports(".doc"));
        Assert.Equal(Lumi.Core.Documents.DocumentFormat.Word, reader.Format);
    }

    [Fact]
    public async Task 標題段落清單與表格依文件順序輸出()
    {
        using var folder = new TempFolder();
        var path = DocxBuilder.Create(folder.Path);

        var result = await new WordDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            "[概覽] 段落 8、表格 1、標題 3\n" +
            "大綱：\n" +
            "# Lumi 文件讀取測試\n" +
            "## 1.1 表格與清單\n" +
            "## 1.2 結尾\n" +
            "---\n" +
            "# Lumi 文件讀取測試\n\n" +
            "這是第一段中文內容，用來確認 UTF-8 與標點（，。！）能正確讀出。\n\n" +
            "## 1.1 表格與清單\n\n" +
            "- 第一項：清單中文\n" +
            "- 第二項：emoji 😀 測試\n\n" +
            "以下是 2×3 表格：\n\n" +
            "| 項目 | 數量 | 備註 |\n" +
            "| --- | --- | --- |\n" +
            "| 蘋果 | 3 | 紅色 |\n\n" +
            "## 1.2 結尾\n\n" +
            "最後一段。",
            result.Content);
    }

    [Fact]
    public async Task 概覽數字與摘要正確()
    {
        using var folder = new TempFolder();
        var path = DocxBuilder.Create(folder.Path);

        var result = await new WordDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.Equal("Word · 8 段落", result.Summary);
        Assert.False(result.Truncated);
        Assert.Null(result.NextOffset);
        Assert.Equal(Lumi.Core.Documents.DocumentFormat.Word, result.Format);
    }

    [Fact]
    public async Task 中文樣式名稱與Title及數字標題都轉成對應層級()
    {
        var styles = StylesXml(
            ("a1", "標題 1"),
            ("Title", "Title"),
            ("Heading3", "heading 3"),
            ("標題2", "xyz"),
            ("Heading6", "heading 6"));
        var body = string.Concat(
            P("一級中文", "a1"),
            P("文件標題", "Title"),
            P("三級", "Heading3"),
            P("二級", "標題2"),
            P("六級", "Heading6"),
            P("普通段落"));

        var content = await ReadRawAsync(RawDocx(body, styles));

        Assert.Equal(
            "[概覽] 段落 6、表格 0、標題 5\n" +
            "大綱：\n# 一級中文\n# 文件標題\n### 三級\n## 二級\n###### 六級\n---\n" +
            "# 一級中文\n\n# 文件標題\n\n### 三級\n\n## 二級\n\n###### 六級\n\n普通段落",
            content);
    }

    [Fact]
    public async Task 沒有樣式表時仍以樣式代號判斷標題()
    {
        var content = await ReadRawAsync(RawDocx(P("只靠代號", "Heading1")));

        Assert.Contains("# 只靠代號", content);
    }

    [Fact]
    public async Task 清單依層級縮排且numId為零的段落不是清單()
    {
        var body = string.Concat(
            ListItem("第一", numId: 1, level: 0),
            ListItem("子項", numId: 1, level: 1),
            ListItem("第二", numId: 1, level: 0),
            ListItem("不是清單", numId: 0, level: 0));

        var content = await ReadRawAsync(RawDocx(body));

        Assert.Equal("[概覽] 段落 4、表格 0、標題 0\n---\n- 第一\n  - 子項\n- 第二\n\n不是清單", content);
    }

    [Fact]
    public async Task 定位字元與換行與符號與超連結的處理()
    {
        var body = string.Concat(
            "<w:p><w:r><w:t>名稱</w:t><w:tab/><w:t>數量</w:t></w:r><w:r><w:br/></w:r><w:r><w:t>第二行</w:t></w:r></w:p>",
            "<w:p><w:r><w:sym w:font=\"Wingdings\" w:char=\"F0FC\"/></w:r><w:hyperlink w:anchor=\"x\"><w:r><w:t>連結文字</w:t></w:r></w:hyperlink></w:p>");

        var content = await ReadRawAsync(RawDocx(body));

        Assert.Equal("[概覽] 段落 2、表格 0、標題 0\n---\n名稱 數量\n第二行\n\n連結文字", content);
    }

    [Fact]
    public async Task 追蹤修訂插入的文字輸出_刪除的文字不輸出()
    {
        using var folder = new TempFolder();
        var path = folder.Write("tracked.docx", TrackedChangesDocx());

        var result = await new WordDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("保留新增的文字", result.Content);
        Assert.DoesNotContain("已刪除的文字", result.Content);
        Assert.Contains("[概覽] 段落 1、", result.Content);   // 只剩刪除內容的段落不算
    }

    [Fact]
    public async Task 表格的管線符號跳脫_多段落用br連接_缺的儲存格補空()
    {
        var nested = TableXml(new[] { new[] { "內1", "內2" } });
        var body = TableXml(new[]
        {
            new[] { "欄A", "欄B|C", "欄D" },
            new[] { "第一行\n第二行", "x" },   // 這列沒有第三欄，要補空
            new[] { "外", "", "z" },
        }, nestedCellIn: (2, 1, nested));

        var content = await ReadRawAsync(RawDocx(body));

        Assert.Equal(
            "[概覽] 段落 0、表格 1、標題 0\n---\n" +
            "| 欄A | 欄B\\|C | 欄D |\n" +
            "| --- | --- | --- |\n" +
            "| 第一行<br>第二行 | x |  |\n" +
            "| 外 | 內1 / 內2 | z |",
            content);
    }

    [Fact]
    public async Task 表格超過兩百列只輸出前兩百列並加說明()
    {
        var rows = Enumerable.Range(1, 250).Select(i => new[] { $"第 {i} 列" }).ToArray();
        var content = await ReadRawAsync(RawDocx(TableXml(rows)));

        Assert.Contains("| 第 200 列 |", content);
        Assert.DoesNotContain("第 201 列", content);
        Assert.Contains("（表格共 250 列，這裡只顯示前 200 列）", content);
    }

    [Fact]
    public async Task 單一儲存格超過五百字元截斷並加省略號()
    {
        var content = await ReadRawAsync(RawDocx(TableXml(new[] { new[] { new string('字', 600) } })));

        Assert.Contains("| " + new string('字', 500) + "… |", content);
        Assert.DoesNotContain(new string('字', 501), content);
    }

    [Fact]
    public async Task 文字方塊的文字放在所在段落之後_備援副本不重複輸出()
    {
        // 記錄實際行為：mc:AlternateContent 的 Choice（wps 文字方塊）會讀到，Fallback（VML）的副本略過
        var body =
            "<w:p><w:r><w:t>段落前</w:t></w:r></w:p>" +
            "<w:p><w:r><mc:AlternateContent><mc:Choice Requires=\"wps\"><w:drawing><wps:wsp><wps:txbx>" +
            "<w:txbxContent><w:p><w:r><w:t>文字方塊內容</w:t></w:r></w:p></w:txbxContent>" +
            "</wps:txbx></wps:wsp></w:drawing></mc:Choice><mc:Fallback><w:pict><v:shape><v:textbox>" +
            "<w:txbxContent><w:p><w:r><w:t>備援文字</w:t></w:r></w:p></w:txbxContent>" +
            "</v:textbox></v:shape></w:pict></mc:Fallback></mc:AlternateContent></w:r></w:p>" +
            "<w:p><w:r><w:t>段落後</w:t></w:r></w:p>";

        var content = await ReadRawAsync(RawDocx(body));

        Assert.Equal("[概覽] 段落 2、表格 0、標題 0\n---\n段落前\n\n[文字方塊] 文字方塊內容\n\n段落後", content);
    }

    [Fact]
    public async Task 沒有mc包裝的VML文字方塊也會輸出()
    {
        var body =
            "<w:p><w:r><w:pict><v:shape><v:textbox><w:txbxContent><w:p><w:r><w:t>舊式文字方塊</w:t></w:r></w:p>" +
            "</w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>";

        var content = await ReadRawAsync(RawDocx(body));

        Assert.Contains("[文字方塊] 舊式文字方塊", content);
    }

    [Fact]
    public async Task 頁首與頁尾的文字不輸出()
    {
        using var folder = new TempFolder();
        var path = folder.Write("header.docx", DocumentWithHeaderBytes());

        var result = await new WordDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.Contains("本文內容", result.Content);
        Assert.DoesNotContain("頁首文字", result.Content);
    }

    [Fact]
    public async Task 大綱最多列四十個標題()
    {
        var body = string.Concat(Enumerable.Range(1, 45).Select(i => P($"標題 {i}", "Heading1")));
        var styles = StylesXml(("Heading1", "heading 1"));

        var result = await ReadDocAsync(RawDocx(body, styles));

        Assert.Contains("[概覽] 段落 45、表格 0、標題 45", result.Content);
        Assert.Contains("…另有 5 個標題", result.Content);
        var outline = result.Content[..result.Content.IndexOf("\n---\n", StringComparison.Ordinal)];
        Assert.Contains("# 標題 40\n", outline);
        Assert.DoesNotContain("# 標題 41\n", outline);
    }

    [Fact]
    public async Task 兩千個段落正常讀完且概覽數字正確()
    {
        var body = string.Concat(Enumerable.Range(1, 2000).Select(i => P($"第 {i} 段")));

        var result = await ReadDocAsync(RawDocx(body));

        Assert.False(result.IsError);
        Assert.Equal("Word · 2000 段落", result.Summary);
        Assert.StartsWith("[概覽] 段落 2000、表格 0、標題 0\n---\n第 1 段\n\n第 2 段", result.Content);
        Assert.DoesNotContain("內文超過", result.Content);
    }

    [Fact]
    public async Task 本文超過五十萬字元時截斷並加說明()
    {
        var text = new string('字', 300);
        var body = string.Concat(Enumerable.Range(1, 2000).Select(_ => P(text)));

        var result = await ReadDocAsync(RawDocx(body));

        Assert.False(result.IsError);
        Assert.Contains("（內文超過 500,000 字元，後面的內容未讀取。）", result.Content);
        Assert.Equal("Word · 2000 段落", result.Summary);
        Assert.False(result.Truncated);   // 切片由 DocumentReader 負責
        Assert.Null(result.NextOffset);
        Assert.True(result.Content.Length < 500_000 + 200, $"實際 {result.Content.Length} 字元");
    }

    [Fact]
    public async Task 空文件回概覽加一行說明()
    {
        var result = await ReadDocAsync(RawDocx(""));

        Assert.False(result.IsError);
        Assert.Equal(Empty, result.Content);
        Assert.Equal("Word · 0 段落", result.Summary);
    }

    [Fact]
    public async Task 只有空白段落的文件視同沒有文字()
    {
        var body = P("   ") + "<w:p/>";

        var content = await ReadRawAsync(RawDocx(body));

        Assert.Equal(Empty, content);
    }

    [Fact]
    public async Task 取消時丟出取消例外()
    {
        using var folder = new TempFolder();
        var path = DocxBuilder.Create(folder.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WordDocumentReader().ReadAsync(new DocumentReadRequest(path), cts.Token));
    }

    [Fact]
    public async Task 損毀與截斷的docx經DocumentReader回固定訊息()
    {
        using var folder = new TempFolder();
        var garbage = folder.Write("garbage.docx", BrokenFiles.Garbage());
        var truncated = folder.Write("truncated.docx", BrokenFiles.TruncatedDocx());
        var noMain = folder.Write("nomain.docx", ZipWithoutMainPart());
        var reader = Reader();

        foreach (var path in new[] { garbage, truncated, noMain })
        {
            var result = await reader.ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

            Assert.True(result.IsError);
            Assert.Equal(DocumentReader.FailedMessage, result.Content);
            Assert.DoesNotContain(folder.Path, result.Content);
        }
    }

    [Fact]
    public async Task 壓縮炸彈經DocumentReader回安全拒絕訊息()
    {
        using var folder = new TempFolder();
        var path = folder.Write("bomb.docx", BrokenFiles.ZipBomb());

        var result = await Reader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(OpenXmlSafety.RejectedMessage, result.Content);
    }

    [Fact]
    public async Task 主部件解壓後超過上限經DocumentReader回固定訊息()
    {
        // 主部件解壓後約 2 MB、壓縮後很小（壓縮比不觸發 zip 檢查）；上限改成 1 MB 測試
        using var folder = new TempFolder();
        var path = folder.Write("large-main.docx", LargeMainPartDocx(2_000_000));

        var ex = await Assert.ThrowsAsync<DocumentRejectedException>(() =>
            new WordDocumentReader(maxMainPartBytes: 1024 * 1024).ReadAsync(new DocumentReadRequest(path), CancellationToken.None));
        Assert.Equal("這份文件的內容過大，已拒絕讀取。", ex.Message);

        var reader = new DocumentReader([new WordDocumentReader(maxMainPartBytes: 1024 * 1024)], new DocumentOptions(), NullLogger<DocumentReader>.Instance);
        var result = await reader.ReadAsync(new DocumentReadRequest(path), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("這份文件的內容過大，已拒絕讀取。", result.Content);
    }

    [Fact]
    public async Task 主部件在上限內的大文件照常讀出()
    {
        using var folder = new TempFolder();
        var path = folder.Write("medium-main.docx", LargeMainPartDocx(300_000));

        var result = await new WordDocumentReader(maxMainPartBytes: 1024 * 1024).ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(new string('a', 300_000), result.Content);
        Assert.DoesNotContain("已拒絕", result.Content);
    }

    [Fact]
    public async Task 含有DTD實體時不展開實體()
    {
        var doctype = "<!DOCTYPE w:document [<!ENTITY secret \"SECRETVALUE\">]>";
        var bytes = RawDocx("<w:p><w:r><w:t>&secret;</w:t></w:r></w:p>", doctype: doctype);
        using var folder = new TempFolder();
        var path = folder.Write("entity.docx", bytes);

        var result = await Reader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

        // 允許的結果：回錯誤（DTD 被拒），或正常讀出但實體沒有被展開
        Assert.DoesNotContain("SECRETVALUE", result.Content);
    }

    // ---------- 測試用的文件產生器（直接組 XML 或用 OpenXml 建立） ----------

    private static DocumentReader Reader() =>
        new([new WordDocumentReader()], new DocumentOptions(), NullLogger<DocumentReader>.Instance);

    private static async Task<string> ReadRawAsync(byte[] bytes)
    {
        var result = await ReadDocAsync(bytes);

        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    /// <summary>把位元組寫進暫存資料夾，經 <see cref="WordDocumentReader"/> 讀取；資料夾用完即刪。</summary>
    private static async Task<DocumentReadResult> ReadDocAsync(byte[] bytes)
    {
        using var folder = new TempFolder();
        var path = folder.Write("sample.docx", bytes);
        return await new WordDocumentReader().ReadAsync(new DocumentReadRequest(path), CancellationToken.None);
    }

    private const string Ns =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\"";

    private static string P(string text, string styleId = null) =>
        "<w:p>" +
        (styleId is null ? "" : $"<w:pPr><w:pStyle w:val=\"{styleId}\"/></w:pPr>") +
        $"<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static string ListItem(string text, int numId, int level) =>
        "<w:p><w:pPr><w:numPr>" +
        $"<w:ilvl w:val=\"{level}\"/><w:numId w:val=\"{numId}\"/>" +
        "</w:numPr></w:pPr>" +
        $"<w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    /// <summary>表格 XML；<paramref name="nestedCellIn"/> 可把指定的 (列, 欄) 換成一個巢狀表格。第一列是表頭。</summary>
    private static string TableXml(string[][] rows, (int Row, int Col, string Xml)? nestedCellIn = null)
    {
        var builder = new StringBuilder("<w:tbl>");
        for (var r = 0; r < rows.Length; r++)
        {
            builder.Append("<w:tr>");
            for (var c = 0; c < rows[r].Length; c++)
            {
                builder.Append("<w:tc>");
                if (nestedCellIn is { } nested && nested.Row == r && nested.Col == c)
                {
                    builder.Append(nested.Xml).Append("<w:p/>");
                }
                else
                {
                    // 儲存格內容用「\n」分隔，這裡拆成多個段落
                    foreach (var line in rows[r][c].Split('\n'))
                        builder.Append($"<w:p><w:r><w:t xml:space=\"preserve\">{line}</w:t></w:r></w:p>");
                }
                builder.Append("</w:tc>");
            }
            builder.Append("</w:tr>");
        }
        return builder.Append("</w:tbl>").ToString();
    }

    /// <summary>
    /// 組 .docx（只有主文件與選擇性的樣式表）。測試內容都是 XML 字串，不靠 OpenXml 的型別，方便寫 mc:AlternateContent 這類結構。
    /// </summary>
    private static byte[] RawDocx(string body, string styles = null, string doctype = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var overrides = "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>";
            if (styles is not null)
                overrides += "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>";

            Write(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" + overrides + "</Types>");

            Write(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            if (styles is not null)
            {
                Write(zip, "word/_rels/document.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");
                Write(zip, "word/styles.xml", styles);
            }

            var declaration = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            Write(zip, "word/document.xml",
                declaration + (doctype ?? "") +
                $"<w:document {Ns}><w:body>{body}</w:body></w:document>");
        }

        return stream.ToArray();
    }

    /// <summary>樣式表：每個 (styleId, 名稱) 一個段落樣式。</summary>
    private static string StylesXml(params (string Id, string Name)[] styles)
    {
        var items = string.Concat(styles.Select(s =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"{s.Id}\"><w:name w:val=\"{s.Name}\"/></w:style>"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<w:styles {Ns}>{items}</w:styles>";
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var entry = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        entry.Write(bytes, 0, bytes.Length);
    }

    /// <summary>主部件含一段 <paramref name="chars"/> 個字元的 "a"（重複字元壓縮很有效，zip 目錄的大小仍是解壓後的大小）。</summary>
    private static byte[] LargeMainPartDocx(int chars)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new Run(new Text(new string('a', chars))))));
        }

        return stream.ToArray();
    }

    /// <summary>有 zip 結構但沒有 word/document.xml 的檔案。</summary>
    private static byte[] ZipWithoutMainPart()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "readme.txt", "沒有主文件");
        }
        return stream.ToArray();
    }

    /// <summary>用 OpenXml 建立：一個有頁首文字的文件。</summary>
    private static byte[] DocumentWithHeaderBytes()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = doc.AddMainDocumentPart();
            var header = main.AddNewPart<HeaderPart>();
            header.Header = new Header(new Paragraph(new Run(new Text("頁首文字"))));
            var headerId = main.GetIdOfPart(header);

            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("本文內容"))),
                new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = headerId })));
        }

        return stream.ToArray();
    }

    /// <summary>用 OpenXml 建立：插入的修訂（w:ins）保留文字，刪除的修訂（w:del）不輸出；第二段只有刪除內容。</summary>
    private static byte[] TrackedChangesDocx()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(
                    new Run(new Text("保留")),
                    new InsertedRun(new Run(new Text("新增的文字")))),
                new Paragraph(new DeletedRun(new Run(new DeletedText("已刪除的文字"))))));
        }

        return stream.ToArray();
    }
}
