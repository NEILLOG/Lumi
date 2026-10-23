#nullable disable   // OpenXml SDK 的導覽屬性標成可為 null；測試直接取值，失敗時以 NullReferenceException 呈現
using System.Security;
using System.Text;
using global::DocumentFormat.OpenXml.Packaging;
using global::DocumentFormat.OpenXml.Presentation;
using Lumi.Core.Documents;
using Lumi.Documents.Tests.Samples;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.Documents.Tests;

using DocumentFormat = Lumi.Core.Documents.DocumentFormat;   // 與 OpenXml 的 DocumentFormat 命名空間同名

public class PowerPointDocumentReaderTests
{
    private const string Namespaces = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";
    private const string GroupHeader = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>";

    [Theory]
    [InlineData(".pptx")]
    [InlineData(".PPTX")]
    public void Supports_pptx_in_any_case(string extension)
    {
        Assert.True(new PowerPointDocumentReader().Supports(extension));
    }

    [Theory]
    [InlineData(".ppt")]
    [InlineData(".docx")]
    [InlineData(".pdf")]
    public void Does_not_support_other_extensions(string extension)
    {
        Assert.False(new PowerPointDocumentReader().Supports(extension));
    }

    [Fact]
    public async Task Sample_deck_has_overview_titles_body_table_and_notes()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        var result = await Read(path);

        Assert.False(result.IsError);
        Assert.Equal(DocumentFormat.PowerPoint, result.Format);
        Assert.Equal("PowerPoint · 3 張投影片", result.Summary);
        Assert.False(result.Truncated);
        Assert.Null(result.NextOffset);

        var content = result.Content;
        Assert.StartsWith("[概覽] 共 3 張投影片\n- 1. Lumi 簡報測試\n- 2. 進度表\n- 3. 第三張\n---\n", content);
        Assert.Contains("## 投影片 1：Lumi 簡報測試\n第一張內文：中文與 English 混排", content);
        Assert.Contains("## 投影片 2：進度表\n| 階段 | 狀態 |\n| --- | --- |\n| M10 | 進行中 |", content);
        Assert.Contains("## 投影片 3：第三張\n第三張內文\n\n[講者備註]\n講者備註：這裡是備註內容", content);

        // 標題只在概覽與各張的標頭出現，不在內文重複
        Assert.Equal(2, Occurrences(content, PptxBuilder.Slide1Title));
        Assert.Equal(1, Occurrences(content, "[講者備註]"));
        Assert.True(content.IndexOf("## 投影片 1", StringComparison.Ordinal) < content.IndexOf("## 投影片 2", StringComparison.Ordinal));
        Assert.True(content.IndexOf("## 投影片 2", StringComparison.Ordinal) < content.IndexOf("## 投影片 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slides_range_reads_only_the_requested_slides_but_overview_lists_all()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        var result = await Read(path, slides: "2");

        Assert.False(result.IsError);
        Assert.Contains("[概覽] 共 3 張投影片", result.Content);
        Assert.Contains("- 3. 第三張", result.Content);
        Assert.Contains("## 投影片 2：進度表", result.Content);
        Assert.DoesNotContain("## 投影片 1：", result.Content);
        Assert.DoesNotContain("## 投影片 3：", result.Content);
        Assert.Contains("本次讀取第 2–2 張，共 3 張", result.Content);
        Assert.Contains("例如 3-3", result.Content);
    }

    [Fact]
    public async Task Slides_range_is_clamped_to_the_deck_size_and_full_coverage_has_no_notice()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        var result = await Read(path, slides: "2-5");

        Assert.False(result.IsError);
        Assert.DoesNotContain("## 投影片 1：", result.Content);
        Assert.Contains("## 投影片 2：進度表", result.Content);
        Assert.Contains("## 投影片 3：第三張", result.Content);
        Assert.DoesNotContain("本次讀取", result.Content);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("0")]
    [InlineData("3-2")]
    [InlineData("1-2-3")]
    [InlineData("abc")]
    public async Task Bad_slides_range_explains_usage_with_the_slide_count(string slides)
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        var result = await Read(path, slides: slides);

        Assert.True(result.IsError);
        Assert.Equal(DocumentFormat.PowerPoint, result.Format);
        Assert.StartsWith("slides 參數不正確", result.Content);
        Assert.Contains("這份簡報共 3 張投影片", result.Content);
        Assert.Contains("3 或 3-5", result.Content);
        Assert.DoesNotContain(folder.Path, result.Content);
    }

    [Fact]
    public async Task Range_longer_than_the_limit_is_rejected()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long.pptx", Deck(Enumerable.Range(1, 60).Select(i => Slide("", Title($"第 {i} 張"))).ToArray()));

        var result = await Read(path, slides: "1-60");

        Assert.True(result.IsError);
        Assert.Contains($"一次最多讀取 {PageRange.MaxSpan} 頁或張", result.Content);
        Assert.Contains("這份簡報共 60 張投影片", result.Content);
    }

    [Fact]
    public async Task Blank_slides_argument_reads_from_the_first_slide()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        var result = await Read(path, slides: "   ");

        Assert.False(result.IsError);
        Assert.Contains("## 投影片 1：Lumi 簡報測試", result.Content);
        Assert.Contains("## 投影片 3：第三張", result.Content);
    }

    [Fact]
    public async Task Include_notes_false_omits_speaker_notes()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);

        var result = await Read(path, includeNotes: false);

        Assert.False(result.IsError);
        Assert.DoesNotContain("[講者備註]", result.Content);
        Assert.DoesNotContain("講者備註：這裡是備註內容", result.Content);
        Assert.Contains("## 投影片 3：第三張\n第三張內文", result.Content);
    }

    [Fact]
    public async Task Notes_skip_thumbnail_and_page_number_placeholders()
    {
        using var folder = new TempFolder();
        var bytes = EditPresentation(PptxBuilder.CreateBytes(), presentation =>
        {
            var relationshipId = presentation.Presentation.SlideIdList.Elements<SlideId>().ElementAt(2).RelationshipId.Value;
            var slide = (SlidePart)presentation.GetPartById(relationshipId);
            slide.NotesSlidePart.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(NotesXml())));
        });
        var path = folder.Write("notes.pptx", bytes);

        var result = await Read(path);

        Assert.Contains("[講者備註]\n備註正文", result.Content);
        Assert.DoesNotContain("縮圖不該出現", result.Content);
        Assert.DoesNotContain("頁碼標記ZZ", result.Content);
    }

    [Fact]
    public async Task Slides_follow_the_slide_id_list_order_not_the_part_names()
    {
        using var folder = new TempFolder();
        var bytes = EditPresentation(PptxBuilder.CreateBytes(), presentation =>
        {
            // 把 sldIdLst 倒過來：檔案裡的 slide1.xml 會排在最後
            var list = presentation.Presentation.SlideIdList;
            var reversed = list.Elements<SlideId>().Reverse().Select(id => (SlideId)id.CloneNode(true)).ToList();
            list.RemoveAllChildren<SlideId>();
            foreach (var id in reversed)
                list.Append(id);
        });
        var path = folder.Write("reversed.pptx", bytes);

        var result = await Read(path);

        Assert.Contains("- 1. 第三張\n- 2. 進度表\n- 3. Lumi 簡報測試", result.Content);
        Assert.True(result.Content.IndexOf("## 投影片 1：第三張", StringComparison.Ordinal) < result.Content.IndexOf("## 投影片 3：Lumi 簡報測試", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Group_shapes_are_read_recursively_in_document_order()
    {
        using var folder = new TempFolder();
        var path = folder.Write("groups.pptx", Deck(
            Slide("",
                Title("群組測試"),
                Group(TextBox("群組文字一"), Group(TextBox("巢狀群組文字"))),
                TextBox("群組之後的文字"))));

        var result = await Read(path);

        Assert.Contains("## 投影片 1：群組測試\n群組文字一\n巢狀群組文字\n群組之後的文字", result.Content);
    }

    [Fact]
    public async Task Hidden_slides_are_marked_in_the_overview_only()
    {
        using var folder = new TempFolder();
        var path = folder.Write("hidden.pptx", Deck(
            Slide("", Title("公開")),
            Slide("show=\"0\"", Title("草稿"), TextBox("隱藏內容")),
            Slide("", Title("結尾"))));

        var result = await Read(path);

        Assert.Contains("- 1. 公開\n- 2. 草稿（已隱藏）\n- 3. 結尾", result.Content);
        Assert.Contains("## 投影片 2：草稿\n隱藏內容", result.Content);
        Assert.DoesNotContain("## 投影片 1：公開（已隱藏）", result.Content);
    }

    [Fact]
    public async Task Pictures_and_charts_are_counted_not_read()
    {
        using var folder = new TempFolder();
        var path = folder.Write("media.pptx", Deck(
            Slide("",
                Title("圖表與圖片"),
                Picture(), Group(Picture(), TextBox("群組內文字")), Chart(), Smart())));

        var result = await Read(path);

        Assert.Contains("## 投影片 1：圖表與圖片\n群組內文字\n（含 2 張圖片）\n（含 2 個圖表）", result.Content);
    }

    [Fact]
    public async Task Slide_without_text_says_so_and_still_counts_its_pictures()
    {
        using var folder = new TempFolder();
        var path = folder.Write("blank.pptx", Deck(
            Slide("", Title("只有標題")),
            Slide("", Picture())));

        var result = await Read(path);

        Assert.Contains("## 投影片 1：只有標題\n（這張投影片沒有文字）", result.Content);
        Assert.Contains("## 投影片 2：（無標題）\n（這張投影片沒有文字）\n（含 1 張圖片）", result.Content);
        Assert.Contains("- 2. （無標題）", result.Content);
    }

    [Fact]
    public async Task Bullet_levels_are_indented_and_plain_paragraphs_stay_plain()
    {
        using var folder = new TempFolder();
        var path = folder.Write("bullets.pptx", Deck(
            Slide("",
                Title("清單"),
                TextBoxXml(
                    Para("重點一", bullet: true, level: 0),
                    Para("子項", bullet: true, level: 1),
                    Para("深一層", bullet: true, level: 2),
                    Para("一般段落"),
                    Para("沒有符號", noBullet: true),
                    Para(""),
                    Para("  前後空白  "),
                    Para("第一行"),
                    Para("第二行")))));

        var result = await Read(path);

        Assert.Contains("## 投影片 1：清單\n- 重點一\n  - 子項\n    - 深一層\n一般段落\n沒有符號\n前後空白\n第一行\n第二行", result.Content);
    }

    [Fact]
    public async Task Table_cells_escape_pipes_and_use_br_for_line_breaks()
    {
        using var folder = new TempFolder();
        var path = folder.Write("table.pptx", Deck(
            Slide("", Title("表格"), Table(
                new[] { "項目", "說明" },
                new[] { "A|B", "第一行\n第二行" },
                new[] { "只有一格" }))));

        var result = await Read(path);

        Assert.Contains("| 項目 | 說明 |\n| --- | --- |\n| A\\|B | 第一行<br>第二行 |\n| 只有一格 |  |", result.Content);
    }

    [Fact]
    public async Task Table_is_limited_to_one_hundred_rows()
    {
        using var folder = new TempFolder();
        var rows = Enumerable.Range(1, 101).Select(i => new[] { $"row-{i}", "x" }).ToArray();
        var path = folder.Write("long-table.pptx", Deck(Slide("", Title("長表格"), Table(rows))));

        var result = await Read(path);

        Assert.Contains("| row-100 | x |", result.Content);
        Assert.DoesNotContain("row-101", result.Content);
        Assert.Contains("（表格還有 1 列未顯示）", result.Content);
    }

    [Fact]
    public async Task Table_cell_is_limited_to_five_hundred_characters()
    {
        using var folder = new TempFolder();
        var path = folder.Write("wide-cell.pptx", Deck(Slide("", Title("長儲存格"), Table(new[] { new string('A', 600) }))));

        var result = await Read(path);

        Assert.Contains(new string('A', 499) + "…", result.Content);
        Assert.DoesNotContain(new string('A', 500), result.Content);
    }

    [Fact]
    public async Task Slide_text_is_limited_to_twenty_thousand_characters()
    {
        using var folder = new TempFolder();
        var paragraphs = Enumerable.Range(0, 250).Select(_ => Para(new string('長', 100))).ToArray();
        var path = folder.Write("long-slide.pptx", Deck(Slide("", Title("很長"), TextBoxXml(paragraphs))));

        var result = await Read(path);

        var section = result.Content[result.Content.IndexOf("## 投影片 1", StringComparison.Ordinal)..];
        Assert.True(section.Length <= 20_000, $"section is {section.Length} characters");
        Assert.EndsWith("（這張投影片超過 20,000 字元，已截斷）", section);
    }

    [Fact]
    public async Task Total_output_is_capped_and_says_where_it_stopped()
    {
        using var folder = new TempFolder();
        var body = string.Concat(Enumerable.Range(0, 120).Select(_ => Para(new string('內', 100))));
        var slides = Enumerable.Range(1, 50).Select(i => Slide("", Title($"第 {i} 張"), TextBoxXml(body))).ToArray();
        var path = folder.Write("big-text.pptx", Deck(slides));

        var result = await Read(path);

        Assert.False(result.IsError);
        Assert.True(result.Content.Length <= 500_000, $"output is {result.Content.Length} characters");
        Assert.Contains("輸出已達 500,000 字元上限", result.Content);
        Assert.Contains("本次讀取第 1–", result.Content);
        Assert.DoesNotContain("## 投影片 50：", result.Content);
    }

    [Fact]
    public async Task Default_read_of_a_long_deck_stops_at_fifty_slides_and_explains_how_to_continue()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long-deck.pptx", LongDeck(300));

        var result = await Read(path);

        var content = result.Content;
        Assert.False(result.IsError);
        Assert.Equal("PowerPoint · 300 張投影片", result.Summary);
        Assert.StartsWith("[概覽] 共 300 張投影片\n- 1. 第 1 張\n", content);
        Assert.Contains("- 100. 第 100 張\n…另有 200 張\n---", content);
        Assert.DoesNotContain("- 101. ", content);
        Assert.Contains("## 投影片 50：第 50 張", content);
        Assert.DoesNotContain("## 投影片 51：", content);
        Assert.Contains("本次讀取第 1–50 張，共 300 張", content);
        Assert.Contains("例如 51-100", content);
    }

    [Fact]
    public async Task Continuing_a_long_deck_with_the_suggested_range_works()
    {
        using var folder = new TempFolder();
        var path = folder.Write("long-deck.pptx", LongDeck(300));

        var result = await Read(path, slides: "51-100");

        Assert.False(result.IsError);
        Assert.Contains("## 投影片 51：第 51 張", result.Content);
        Assert.Contains("## 投影片 100：第 100 張", result.Content);
        Assert.DoesNotContain("## 投影片 101：", result.Content);
        Assert.Contains("例如 101-150", result.Content);
    }

    [Fact]
    public async Task Deck_without_slides_has_only_the_overview()
    {
        using var folder = new TempFolder();
        var path = folder.Write("empty-deck.pptx", Deck());

        var result = await Read(path);

        Assert.False(result.IsError);
        Assert.Equal("[概覽] 共 0 張投影片\n---", result.Content);
        Assert.Equal("PowerPoint · 0 張投影片", result.Summary);
    }

    [Fact]
    public async Task Titles_with_emoji_and_chinese_are_kept()
    {
        using var folder = new TempFolder();
        var path = folder.Write("emoji.pptx", Deck(Slide("", Title("🎉 開場"), TextBox("中文 🙂 內文"))));

        var result = await Read(path);

        Assert.Contains("- 1. 🎉 開場", result.Content);
        Assert.Contains("## 投影片 1：🎉 開場\n中文 🙂 內文", result.Content);
    }

    [Fact]
    public async Task Garbage_file_with_pptx_name_returns_the_fixed_failure_message()
    {
        using var folder = new TempFolder();
        var path = folder.Write("garbage.pptx", BrokenFiles.Garbage());

        var result = await ReadThroughDocumentReader(path);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
        Assert.DoesNotContain(folder.Path, result.Content);
    }

    [Fact]
    public async Task Empty_file_returns_the_fixed_failure_message()
    {
        using var folder = new TempFolder();
        var path = folder.Write("empty.pptx", BrokenFiles.Empty());

        var result = await ReadThroughDocumentReader(path);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
    }

    [Fact]
    public async Task Truncated_pptx_returns_the_fixed_failure_message()
    {
        using var folder = new TempFolder();
        var bytes = PptxBuilder.CreateBytes();
        var path = folder.Write("truncated.pptx", bytes[..(bytes.Length / 2)]);

        var result = await ReadThroughDocumentReader(path);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
    }

    [Fact]
    public async Task Valid_zip_that_is_not_a_presentation_returns_the_fixed_failure_message()
    {
        using var folder = new TempFolder();
        var path = folder.Write("not-a-deck.pptx", BrokenFiles.TooManyEntries(entries: 5));

        var result = await ReadThroughDocumentReader(path);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.FailedMessage, result.Content);
    }

    [Fact]
    public async Task Zip_bomb_is_rejected_before_reading_slides()
    {
        using var folder = new TempFolder();
        var path = folder.Write("bomb.pptx", BrokenFiles.ZipBomb());

        var ex = await Assert.ThrowsAsync<DocumentRejectedException>(() => Read(path));

        Assert.Equal(OpenXmlSafety.RejectedMessage, ex.Message);
    }

    [Fact]
    public async Task Too_many_zip_entries_are_rejected()
    {
        using var folder = new TempFolder();
        var path = folder.Write("many.pptx", BrokenFiles.TooManyEntries());

        var result = await ReadThroughDocumentReader(path);

        Assert.True(result.IsError);
        Assert.Equal(OpenXmlSafety.RejectedMessage, result.Content);
    }

    [Fact]
    public async Task Password_protected_file_returns_the_password_message()
    {
        using var folder = new TempFolder();
        var path = folder.Write("locked.pptx", BrokenFiles.PasswordProtectedOpenXml());

        var result = await ReadThroughDocumentReader(path);

        Assert.True(result.IsError);
        Assert.Equal(DocumentReader.PasswordMessage, result.Content);
    }

    [Fact]
    public async Task Cancelled_read_throws_operation_canceled()
    {
        using var folder = new TempFolder();
        var path = PptxBuilder.Create(folder.Path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PowerPointDocumentReader().ReadAsync(new DocumentReadRequest(path), cts.Token));
    }

    // ---------- 測試用的讀取與樣本產生輔助方法 ----------

    private static Task<DocumentReadResult> Read(string path, string slides = null, bool includeNotes = true) =>
        new PowerPointDocumentReader().ReadAsync(new DocumentReadRequest(path, Slides: slides, IncludeNotes: includeNotes), CancellationToken.None);

    /// <summary>經過 DocumentReader（會把例外轉成固定訊息）讀取。</summary>
    private static Task<DocumentReadResult> ReadThroughDocumentReader(string path) =>
        new DocumentReader([new PowerPointDocumentReader()], new DocumentOptions(), NullLogger<DocumentReader>.Instance)
            .ReadAsync(new DocumentReadRequest(path), CancellationToken.None);

    /// <summary>可以寫入的記憶體串流（new MemoryStream(byte[]) 不能擴充，SDK 存檔時會失敗）。</summary>
    private static MemoryStream Writable(byte[] bytes)
    {
        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    /// <summary>用 PptxBuilder 的範本開啟並套用 <paramref name="edit"/>，回傳新的位元組。</summary>
    private static byte[] EditPresentation(byte[] source, Action<PresentationPart> edit)
    {
        using var stream = Writable(source);
        using (var document = PresentationDocument.Open(stream, isEditable: true))
        {
            edit(document.PresentationPart);
            document.PresentationPart.Presentation.Save();
        }
        return stream.ToArray();
    }

    /// <summary>從範本組一份簡報：刪掉範本的投影片清單，依序加入每個 XML 當一張投影片（讀取器只看投影片與 sldIdLst）。</summary>
    private static byte[] Deck(params string[] slideXml)
    {
        using var stream = Writable(PptxBuilder.CreateBytes());
        using (var document = PresentationDocument.Open(stream, isEditable: true))
        {
            var presentation = document.PresentationPart;
            presentation.Presentation.SlideIdList.RemoveAllChildren<SlideId>();
            foreach (var xml in slideXml)
                AddSlide(presentation, xml);
            presentation.Presentation.Save();
        }
        return stream.ToArray();
    }

    private static byte[] LongDeck(int count) =>
        Deck(Enumerable.Range(1, count).Select(i => Slide("", Title($"第 {i} 張"))).ToArray());

    private static void AddSlide(PresentationPart presentation, string xml)
    {
        var part = presentation.AddNewPart<SlidePart>();
        part.FeedData(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var list = presentation.Presentation.SlideIdList;
        var next = list.Elements<SlideId>().Select(id => id.Id.Value).DefaultIfEmpty(255u).Max() + 1;
        list.Append(new SlideId { Id = next, RelationshipId = presentation.GetIdOfPart(part) });
    }

    private static string Slide(string attributes, params string[] shapes) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        $"<p:sld {Namespaces} {attributes}><p:cSld><p:spTree>{GroupHeader}{string.Concat(shapes)}</p:spTree></p:cSld>" +
        "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";

    private static string NotesXml() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
        $"<p:notes {Namespaces}><p:cSld><p:spTree>{GroupHeader}" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"縮圖\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>縮圖不該出現</a:t></a:r></a:p></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"頁碼\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"sldNum\" sz=\"quarter\" idx=\"5\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>頁碼標記ZZ</a:t></a:r></a:p></p:txBody></p:sp>" +
        "<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"備註\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        "<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>備註正文</a:t></a:r></a:p></p:txBody></p:sp>" +
        "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>";

    private static string Title(string text) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"標題\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"title\"/></p:nvPr></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{Para(text)}</p:txBody></p:sp>";

    /// <summary>文字方塊（沒有佔位符），每個字串是一段純文字。</summary>
    private static string TextBox(params string[] texts) =>
        TextBoxXml(texts.Select(t => Para(t)).ToArray());

    /// <summary>文字方塊，參數是已經組好的段落（<see cref="Para"/> 的結果）。</summary>
    private static string TextBoxXml(params string[] paragraphs) =>
        "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"文字\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr/>" +
        $"<p:txBody><a:bodyPr/><a:lstStyle/>{string.Concat(paragraphs)}</p:txBody></p:sp>";

    private static string Group(params string[] children) =>
        "<p:grpSp><p:nvGrpSpPr><p:cNvPr id=\"4\" name=\"群組\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/>" +
        string.Concat(children) + "</p:grpSp>";

    private static string Picture() =>
        "<p:pic><p:nvPicPr><p:cNvPr id=\"5\" name=\"圖片\"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>" +
        "<p:blipFill><a:blip/><a:stretch><a:fillRect/></a:stretch></p:blipFill><p:spPr/></p:pic>";

    private static string Chart() => GraphicFrame("http://schemas.openxmlformats.org/drawingml/2006/chart", "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" r:id=\"rId99\"/>");

    private static string Smart() => GraphicFrame("http://schemas.openxmlformats.org/drawingml/2006/diagram", "<dgm:relIds xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" r:dm=\"rId98\"/>");

    private static string GraphicFrame(string uri, string content) =>
        "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"6\" name=\"圖\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
        "<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1\" cy=\"1\"/></p:xfrm>" +
        $"<a:graphic><a:graphicData uri=\"{uri}\">{content}</a:graphicData></a:graphic></p:graphicFrame>";

    /// <summary>表格：第一列是表頭；一格的文字可以用 \n 分段（變成多個段落）。</summary>
    private static string Table(params string[][] rows)
    {
        var rowXml = string.Concat(rows.Select(row =>
            "<a:tr h=\"370840\">" +
            string.Concat(row.Select(cell =>
                "<a:tc><a:txBody><a:bodyPr/><a:lstStyle/>" +
                string.Concat(cell.Split('\n').Select(line => $"<a:p><a:r><a:rPr lang=\"zh-TW\"/><a:t>{SecurityElement.Escape(line)}</a:t></a:r></a:p>")) +
                "</a:txBody><a:tcPr/></a:tc>")) +
            "</a:tr>"));
        return "<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"7\" name=\"表格\"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>" +
               "<p:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"1\" cy=\"1\"/></p:xfrm>" +
               "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\"><a:tbl><a:tblPr firstRow=\"1\"/>" +
               "<a:tblGrid><a:gridCol w=\"1000\"/><a:gridCol w=\"1000\"/></a:tblGrid>" + rowXml +
               "</a:tbl></a:graphicData></a:graphic></p:graphicFrame>";
    }

    /// <summary>段落。<paramref name="bullet"/> 為 true 時有 buChar；<paramref name="noBullet"/> 為 true 時寫 buNone；兩者都不是就沒有項目符號屬性。</summary>
    private static string Para(string text, bool bullet = false, int? level = null, bool noBullet = false)
    {
        var properties = bullet
            ? $"<a:pPr{(level is null ? "" : $" lvl=\"{level}\"")}><a:buChar char=\"•\"/></a:pPr>"
            : noBullet ? "<a:pPr><a:buNone/></a:pPr>" : "";
        if (text.Length == 0)
            return $"<a:p>{properties}<a:endParaRPr lang=\"zh-TW\"/></a:p>";
        return $"<a:p>{properties}<a:r><a:rPr lang=\"zh-TW\"/><a:t>{SecurityElement.Escape(text)}</a:t></a:r></a:p>";
    }
}
