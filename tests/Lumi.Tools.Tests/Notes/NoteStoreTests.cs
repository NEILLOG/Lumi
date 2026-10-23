using Lumi.Core.Notes;
using Lumi.Tools.Notes;

namespace Lumi.Tools.Tests.Notes;

/// <summary>M10 10B：筆記檔案的格式（與舊版相同）、追加、讀取、其他檔案與符號連結的保護。</summary>
public sealed class NoteStoreTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 23);
    private static readonly TimeOnly At = new(9, 5);

    private readonly NoteTempRoot _root = new();
    private readonly FolderBox _folder;
    private readonly NoteOptions _options = new();

    public NoteStoreTests() => _folder = new FolderBox(_root.Dir("notes"));

    public void Dispose() => _root.Dispose();

    private NoteStore Store(NoteOptions? options = null) => new(_folder, options ?? _options);

    private string FileOf(DateOnly date) => Path.Combine(_folder.Folder, date.ToString("yyyy-MM-dd") + ".md");

    private string Raw(DateOnly date) => NoteFiles.ReadRaw(FileOf(date));

    // ---- 格式 ----

    [Fact]
    public async Task NewFile_HasTitleLine_BlankLine_AndEntry_Utf8WithoutBom_Lf()
    {
        await Store().AppendAsync(Day, At, "開發", "買牛奶", default);

        Assert.Equal("# 2026-10-23\n\n- 09:05 [開發] 買牛奶\n", Raw(Day));
    }

    [Fact]
    public async Task SecondAppend_OnlyAddsItsEntry()
    {
        var store = Store();
        await store.AppendAsync(Day, At, null, "第一則", default);
        await store.AppendAsync(Day, new TimeOnly(10, 30), "會議", "第二則", default);

        Assert.Equal("# 2026-10-23\n\n- 09:05 第一則\n- 10:30 [會議] 第二則\n", Raw(Day));
    }

    [Fact]
    public async Task CategoryOmitted_WritesNoBrackets()
    {
        await Store().AppendAsync(Day, At, null, "沒有分類", default);

        Assert.Equal("# 2026-10-23\n\n- 09:05 沒有分類\n", Raw(Day));
    }

    [Fact]
    public async Task MultiLineContent_ContinuationLinesAreIndentedTwoSpaces_BlankLinesStayEmpty()
    {
        await Store().AppendAsync(Day, At, null, "第一行\n第二行\n\n第四行", default);

        Assert.Equal("# 2026-10-23\n\n- 09:05 第一行\n  第二行\n\n  第四行\n", Raw(Day));
    }

    [Fact]
    public async Task CrLfInContent_BecomesLf()
    {
        await Store().AppendAsync(Day, At, null, "a\r\nb", default);

        var raw = Raw(Day);
        Assert.DoesNotContain('\r', raw);
        Assert.Equal("# 2026-10-23\n\n- 09:05 a\n  b\n", raw);
    }

    [Fact]
    public async Task ContinuationLineStartingWithBullet_IsIndented_SoItStaysOneEntry()
    {
        var store = Store();
        await store.AppendAsync(Day, At, null, "項目\n- 子項", default);

        var days = await store.ReadAsync(Day, Day, null, default);
        var entry = Assert.Single(Assert.Single(days).Entries);
        Assert.Equal("項目\n- 子項", entry.Content);
    }

    [Fact]
    public async Task MissingFinalNewline_IsAddedBeforeTheNextEntry()
    {
        Directory.CreateDirectory(_folder.Folder);
        File.WriteAllText(FileOf(Day), "# 2026-10-23\n\n- 08:00 舊的");

        await Store().AppendAsync(Day, At, null, "新的", default);

        Assert.Equal("# 2026-10-23\n\n- 08:00 舊的\n- 09:05 新的\n", Raw(Day));
    }

    [Fact]
    public async Task EmptyExistingFile_GetsTitleLine()
    {
        Directory.CreateDirectory(_folder.Folder);
        File.WriteAllText(FileOf(Day), "");

        await Store().AppendAsync(Day, At, null, "內容", default);

        Assert.Equal("# 2026-10-23\n\n- 09:05 內容\n", Raw(Day));
    }

    [Fact]
    public async Task Unicode_RoundTrips()
    {
        const string content = "中文 日本語 한국어 🎉 é";
        var store = Store();
        await store.AppendAsync(Day, At, "分類 ✓", content, default);

        var entry = Assert.Single(Assert.Single(await store.ReadAsync(Day, Day, null, default)).Entries);
        Assert.Equal(content, entry.Content);
        Assert.Equal("分類 ✓", entry.Category);
    }

    [Fact]
    public async Task FolderIsCreatedOnFirstAppend()
    {
        Assert.False(Directory.Exists(_folder.Folder));

        await Store().AppendAsync(Day, At, null, "內容", default);

        Assert.True(File.Exists(FileOf(Day)));
    }

    [Fact]
    public async Task ConcurrentAppends_DoNotInterleave()
    {
        var store = Store();
        var writes = Enumerable.Range(0, 30).Select(i =>
            store.AppendAsync(Day, new TimeOnly(8, 0), null, $"第 {i} 則\n第二行 {i}\n第三行 {i}", default));
        await Task.WhenAll(writes);

        var entries = Assert.Single(await store.ReadAsync(Day, Day, null, default)).Entries;
        Assert.Equal(30, entries.Count);
        foreach (var entry in entries)
        {
            var number = entry.Content.Split('\n')[0]["第 ".Length..^" 則".Length];
            Assert.Equal($"第 {number} 則\n第二行 {number}\n第三行 {number}", entry.Content);
        }
    }

    [Fact]
    public async Task AppendAcrossStores_ToSameFolder_KeepsEveryEntry()
    {
        await Store().AppendAsync(Day, At, null, "甲", default);
        await Store().AppendAsync(Day, At, null, "乙", default);

        var entries = Assert.Single(await Store().ReadAsync(Day, Day, null, default)).Entries;
        Assert.Equal(["甲", "乙"], entries.Select(e => e.Content));
    }

    [Fact]
    public async Task FolderChangedInSettings_TakesEffectOnTheNextWrite()
    {
        var store = Store();
        await store.AppendAsync(Day, At, null, "舊資料夾", default);

        _folder.Folder = _root.Dir("moved");
        await store.AppendAsync(Day, At, null, "新資料夾", default);

        // 讀取也跟著新資料夾走：舊資料夾的那一則不在新位置，新資料夾只有新的那則
        Assert.Equal(_root.Dir("moved"), store.FolderPath);
        Assert.Equal(["新資料夾"], Assert.Single(await store.ReadAsync(Day, Day, null, default)).Entries.Select(e => e.Content));
        Assert.Equal("# 2026-10-23\n\n- 09:05 新資料夾\n", NoteFiles.ReadRaw(Path.Combine(_root.Dir("moved"), "2026-10-23.md")));
    }

    // ---- 讀取 ----

    [Fact]
    public async Task Read_ReturnsDaysInRange_OldestFirst_OnlyDaysWithNotes()
    {
        var store = Store();
        await store.AppendAsync(new DateOnly(2026, 10, 25), At, null, "最後", default);
        await store.AppendAsync(new DateOnly(2026, 10, 21), At, null, "最早", default);
        await store.AppendAsync(new DateOnly(2026, 10, 23), At, null, "中間", default);
        await store.AppendAsync(new DateOnly(2026, 10, 30), At, null, "範圍外", default);

        var days = await store.ReadAsync(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 25), null, default);

        Assert.Equal([new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 23), new DateOnly(2026, 10, 25)], days.Select(d => d.Date));
    }

    [Fact]
    public async Task Read_Keyword_MatchesContentOrCategory_IgnoringCase_AndDropsEmptyDays()
    {
        var store = Store();
        await store.AppendAsync(Day, At, null, "Buy MILK", default);
        await store.AppendAsync(Day, At, "Shopping", "雞蛋", default);
        await store.AppendAsync(new DateOnly(2026, 10, 24), At, null, "沒有關係", default);

        var days = await store.ReadAsync(Day, new DateOnly(2026, 10, 24), "milk", default);
        Assert.Equal(["Buy MILK"], Assert.Single(days).Entries.Select(e => e.Content));

        var byCategory = await store.ReadAsync(Day, new DateOnly(2026, 10, 24), "shop", default);
        Assert.Equal(["雞蛋"], Assert.Single(byCategory).Entries.Select(e => e.Content));

        Assert.Empty(await store.ReadAsync(Day, new DateOnly(2026, 10, 24), "不存在", default));
    }

    [Fact]
    public async Task Read_MissingFolder_ReturnsEmpty()
    {
        Assert.Empty(await Store().ReadAsync(Day, Day, null, default));
    }

    [Fact]
    public async Task Read_ReversedRange_ReturnsEmpty()
    {
        await Store().AppendAsync(Day, At, null, "內容", default);

        Assert.Empty(await Store().ReadAsync(Day.AddDays(1), Day, null, default));
    }

    [Fact]
    public async Task OldFormatSample_ReadsEveryEntry()
    {
        // 依 plan/09-convert-from-old.md 第 6.2 節手寫的舊版檔案
        Directory.CreateDirectory(_folder.Folder);
        File.WriteAllText(Path.Combine(_folder.Folder, "2026-10-20.md"),
            "# 2026-10-20\n\n" +
            "- 08:30 [開發] 修好登入的 bug\n" +
            "  原因是 token 過期沒有更新\n" +
            "- 14:00 [待辦] 寄報價單給王經理\n" +
            "- 沒有時間的一般筆記\n" +
            "* 星號開頭的項目\n");

        var entries = Assert.Single(await Store().ReadAsync(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 20), null, default)).Entries;

        Assert.Equal(4, entries.Count);
        Assert.Equal(new TimeOnly(8, 30), entries[0].Time);
        Assert.Equal("開發", entries[0].Category);
        Assert.Equal("修好登入的 bug\n原因是 token 過期沒有更新", entries[0].Content);
        Assert.Equal(new TimeOnly(14, 0), entries[1].Time);
        Assert.Equal("待辦", entries[1].Category);
        Assert.Equal("寄報價單給王經理", entries[1].Content);
        Assert.Null(entries[2].Time);
        Assert.Null(entries[2].Category);
        Assert.Equal("沒有時間的一般筆記", entries[2].Content);
        Assert.Equal("星號開頭的項目", entries[3].Content);
    }

    [Fact]
    public void ParseEntries_TextBeforeFirstBullet_BecomesItsOwnEntry()
    {
        var entries = NoteStore.ParseEntries("# 2026-10-20\n\n前言文字\n- 08:00 項目\n");

        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].Time);
        Assert.Equal("前言文字", entries[0].Content);
        Assert.Equal("項目", entries[1].Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("# 2026-10-20")]
    [InlineData("# 2026-10-20\n\n")]
    [InlineData("\0\0\0\0")]
    [InlineData("- ")]
    [InlineData("- 25:99 [未閉合 內容")]
    [InlineData("-\n-\n*")]
    public void UnexpectedContent_DoesNotThrow(string text)
    {
        var entries = NoteStore.ParseEntries(text);

        Assert.All(entries, e => Assert.NotNull(e.Content));
    }

    [Fact]
    public void TitleOnly_HasNoEntries()
    {
        Assert.Empty(NoteStore.ParseEntries("# 2026-10-20\n\n"));
    }

    [Fact]
    public async Task GarbageAndEmptyFiles_AreReadWithoutFailing()
    {
        Directory.CreateDirectory(_folder.Folder);
        File.WriteAllBytes(FileOf(new DateOnly(2026, 10, 20)), [0xFF, 0xFE, 0x00, 0x81, 0xC3, 0x28, 0x00]);
        File.WriteAllBytes(FileOf(new DateOnly(2026, 10, 21)), []);
        File.WriteAllText(FileOf(new DateOnly(2026, 10, 22)), "# 2026-10-22\n");

        var days = await Store().ReadAsync(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 22), null, default);

        Assert.All(days, d => Assert.NotEmpty(d.Entries));
    }

    [Fact]
    public async Task UtfBomFile_IsReadWithoutTheBom()
    {
        Directory.CreateDirectory(_folder.Folder);
        File.WriteAllBytes(FileOf(Day), [0xEF, 0xBB, 0xBF, .. "# 2026-10-23\n\n- 09:05 有 BOM\n"u8.ToArray()]);

        var entry = Assert.Single(Assert.Single(await Store().ReadAsync(Day, Day, null, default)).Entries);
        Assert.Equal("有 BOM", entry.Content);
    }

    [Fact]
    public async Task OversizedFile_IsSkipped()
    {
        Directory.CreateDirectory(_folder.Folder);
        File.WriteAllText(FileOf(Day), "# 2026-10-23\n\n- 09:05 " + new string('字', 200) + "\n");
        var options = new NoteOptions { MaxFileBytes = 100 };

        Assert.Empty(await Store(options).ReadAsync(Day, Day, null, default));
    }

    [Fact]
    public async Task OtherFiles_AreNeverReadOrWritten()
    {
        Directory.CreateDirectory(_folder.Folder);
        var others = new Dictionary<string, string>
        {
            ["README.md"] = "# 我的 vault\n- 09:00 不是筆記\n",
            ["2026-10-23.txt"] = "- 09:00 副檔名不對\n",
            ["notes.md"] = "- 09:00 名稱不對\n",
            ["2026-13-45.md"] = "- 09:00 日期不存在\n",
            ["2026-10-23.md.bak"] = "- 09:00 備份\n",
            ["2026-10-24.MD"] = "- 09:00 大寫副檔名\n",
        };
        foreach (var (name, text) in others)
            File.WriteAllText(Path.Combine(_folder.Folder, name), text);
        Directory.CreateDirectory(Path.Combine(_folder.Folder, "2026-10-23.md.d"));

        var store = Store();
        await store.AppendAsync(Day, At, null, "新的", default);
        var days = await store.ReadAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, default);

        Assert.Equal([Day], days.Select(d => d.Date));
        foreach (var (name, text) in others)
            Assert.Equal(text, File.ReadAllText(Path.Combine(_folder.Folder, name)));
    }

    // ---- 大小上限（寫入也要擋，否則寫得進去、讀不出來） ----

    [Fact]
    public async Task AppendAtTheSizeLimit_IsWritten_AndReadsBack()
    {
        const int limit = 256 * 1024;
        Directory.CreateDirectory(_folder.Folder);
        // 既有檔案 = 上限 - 10 位元組；"- 09:05 x\n" 正好 10 位元組，寫完剛好等於上限
        File.WriteAllText(FileOf(Day), "# 2026-10-23\n\n- 09:00 " + new string('a', limit - 33) + "\n");

        await Store().AppendAsync(Day, At, null, "x", default);

        Assert.Equal(limit, new FileInfo(FileOf(Day)).Length);
        var entries = Assert.Single(await Store().ReadAsync(Day, Day, null, default)).Entries;
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task AppendOverTheSizeLimit_IsRejected_AndFileIsUnchanged()
    {
        const int limit = 256 * 1024;
        Directory.CreateDirectory(_folder.Folder);
        // 既有檔案正好等於上限（標題 14 + "- 09:00 " 8 + 內容 + "\n"），再寫任何一則都會超過
        File.WriteAllText(FileOf(Day), "# 2026-10-23\n\n- 09:00 " + new string('a', limit - 23) + "\n");
        var before = Raw(Day);

        var error = await Assert.ThrowsAsync<NoteStoreException>(() => Store().AppendAsync(Day, At, null, "x", default));

        Assert.Equal("這天的筆記已經太長（上限 256 KB），請改記到其他日期。", error.UserMessage);
        Assert.Equal(before, Raw(Day));
        Assert.Single(Assert.Single(await Store().ReadAsync(Day, Day, null, default)).Entries);
    }

    // ---- 符號連結 ----

    [SkippableFact]
    public async Task SymbolicLinkNoteFile_IsRejectedForWrite_AndSkippedForRead()
    {
        Directory.CreateDirectory(_folder.Folder);
        var target = _root.Dir("outside.md");
        File.WriteAllText(target, "原本的內容\n");
        Skip.IfNot(NoteFiles.TryLink(FileOf(Day), target), "這個環境不能建立符號連結");

        var store = Store();
        var error = await Assert.ThrowsAsync<NoteStoreException>(() => store.AppendAsync(Day, At, null, "不該寫進去", default));

        Assert.Contains("符號連結", error.UserMessage);
        Assert.Equal("原本的內容\n", File.ReadAllText(target));
        Assert.Empty(await store.ReadAsync(Day, Day, null, default));
    }

    // ---- 寫入失敗 ----

    [SkippableFact]
    public async Task UnwritableFolder_ThrowsNoteStoreException_WithoutPaths()
    {
        // 資料夾的位置被一個檔案佔住：建立資料夾會失敗
        File.WriteAllText(_root.Dir("blocker"), "x");
        _folder.Folder = Path.Combine(_root.Dir("blocker"), "notes");

        var error = await Assert.ThrowsAsync<NoteStoreException>(() => Store().AppendAsync(Day, At, null, "內容", default));

        Assert.DoesNotContain(_root.Path, error.UserMessage);
    }
}
