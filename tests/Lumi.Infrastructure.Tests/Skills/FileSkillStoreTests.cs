using System.IO.Compression;
using System.Text;
using Lumi.Core.Models;
using Lumi.Core.Skills;
using Lumi.Infrastructure.Settings;
using Lumi.Infrastructure.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lumi.Infrastructure.Tests.Skills;

/// <summary>M29 包 29A：<see cref="FileSkillStore"/> 的存檔、改名、讀取、匯入（資料夾與 zip）與啟用上限。用暫存資料夾與真的 <see cref="JsonModelCatalog"/>。</summary>
public sealed class FileSkillStoreTests : IDisposable
{
    private const string ScriptsWarningPart = "腳本或程式";
    private const string FieldsWarningPart = "工具權限";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lumi-file-skills-" + Guid.NewGuid().ToString("N"));
    private readonly JsonModelCatalog _catalog;
    private readonly FileSkillStore _store;

    public FileSkillStoreTests()
    {
        Directory.CreateDirectory(_root);
        _catalog = new JsonModelCatalog(
            Options.Create(new ModelCatalogOptions { SettingsPath = Path.Combine(_root, "settings.json") }),
            new InMemorySecretStore(),
            NullLogger<JsonModelCatalog>.Instance);
        _store = new FileSkillStore(Folder, _catalog, NullLogger<FileSkillStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder => Path.Combine(_root, "skills");

    private static SkillDraft Draft(string name, string body = "1. 步驟", string? original = null) =>
        new(name, "每週五整理本週工作時使用", body, original);

    private void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }

    private void WriteSkill(string dir, string name, string description, string body, string? extra = null)
    {
        var other = extra is null ? null : new[] { extra };
        WriteFile(Path.Combine(dir, "SKILL.md"), SkillFrontmatter.Write(name, description, body, other));
    }

    private int StagingCount() =>
        Directory.Exists(Folder) ? Directory.EnumerateDirectories(Folder, ".import-*").Count() : 0;

    private string CreateZip(string fileName, Action<ZipArchive> fill)
    {
        var path = Path.Combine(_root, fileName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            fill(archive);
        return path;
    }

    private static void AddText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static void AddBytes(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static string Md(string name, string body = "1. 步驟", string? extra = null) =>
        SkillFrontmatter.Write(name, "每週五整理本週工作時使用", body, extra is null ? null : [extra]);

    // ---------- 清單與快取 ----------

    [Fact]
    public async Task List_MissingFolder_IsEmpty_AndNotCreated()
    {
        var list = await _store.ListAsync(default);

        Assert.Empty(list);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task Cache_ShowsDiskChangesOnlyAfterRefresh()
    {
        WriteSkill(Path.Combine(Folder, "週報"), "週報", "d", "步驟");
        Assert.Single(await _store.ListAsync(default));

        WriteSkill(Path.Combine(Folder, "會議"), "會議", "d", "步驟");
        Assert.Single(await _store.ListAsync(default));

        var changed = 0;
        _store.Changed += (_, _) => changed++;
        await _store.RefreshAsync(default);

        Assert.Equal(2, (await _store.ListAsync(default)).Count);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Changed_IsRaisedOnSaveDeleteRefresh_AndWhenCatalogChanges()
    {
        var changed = 0;
        _store.Changed += (_, _) => changed++;

        await _store.SaveAsync(Draft("週報"), default);
        await _store.DeleteAsync("週報", default);
        await _store.RefreshAsync(default);
        await _catalog.SetSkillEnabledAsync("其他", false);

        Assert.Equal(4, changed);
    }

    [Fact]
    public async Task BrokenSkills_AreListedWithAProblem_AndNeverThrow()
    {
        WriteSkill(Path.Combine(Folder, "好的"), "好的", "描述", "步驟");
        Directory.CreateDirectory(Path.Combine(Folder, "沒有檔案"));
        WriteFile(Path.Combine(Folder, "沒描述", "SKILL.md"), "---\nname: 沒描述\n---\n步驟");
        WriteFile(Path.Combine(Folder, "名稱不合", "SKILL.md"), "---\nname: a/b\ndescription: d\n---\n步驟");
        WriteFile(Path.Combine(Folder, "內文太長", "SKILL.md"), Md("內文太長", new string('步', SkillRules.MaxBodyLength + 1)));
        WriteFile(Path.Combine(Folder, "太大", "SKILL.md"), Md("太大", new string('a', 110 * 1024)));

        var list = await _store.ListAsync(default);

        Assert.Equal(6, list.Count);
        Assert.Null(list.Single(s => s.Name == "好的").Problem);
        Assert.Contains("SKILL.md", list.Single(s => s.Name == "沒有檔案").Problem);
        Assert.NotNull(list.Single(s => s.Name == "沒描述").Problem);
        Assert.NotNull(list.Single(s => s.Name == "a/b").Problem);   // 名稱以 frontmatter 為準
        Assert.NotNull(list.Single(s => s.Name == "內文太長").Problem);
        Assert.Contains("太大", list.Single(s => s.Name == "太大").Problem);

        var doc = await _store.GetAsync("沒描述", default);
        Assert.NotNull(doc);
        Assert.NotNull(doc.Info.Problem);
        Assert.False(doc.Info.IsUsable);
    }

    [Fact]
    public async Task NameFromFrontmatter_WinsOverFolderName()
    {
        WriteSkill(Path.Combine(Folder, "資料夾甲"), "週報", "d", "步驟");

        var list = await _store.ListAsync(default);

        Assert.Equal(["週報"], list.Select(s => s.Name));
        Assert.NotNull(await _store.GetAsync("週報", default));
    }

    [Fact]
    public async Task NameFromFolder_WhenFrontmatterHasNoName()
    {
        WriteFile(Path.Combine(Folder, "資料夾名", "SKILL.md"), "---\ndescription: d\n---\n步驟");

        var list = await _store.ListAsync(default);

        Assert.Equal(["資料夾名"], list.Select(s => s.Name));
        Assert.Null(list[0].Problem);
    }

    [Fact]
    public async Task SameNameFolders_SecondOneIsAProblem()
    {
        WriteSkill(Path.Combine(Folder, "x1"), "同名", "d", "第一個");
        WriteSkill(Path.Combine(Folder, "x2"), "同名", "d", "第二個");

        var list = await _store.ListAsync(default);

        Assert.Equal(2, list.Count);
        Assert.Null(list[0].Problem);
        Assert.Equal("和另一個技能同名。", list[1].Problem);
        Assert.Equal("第一個", (await _store.GetAsync("同名", default))!.Body);
    }

    [Fact]
    public async Task DotFolders_AreIgnored()
    {
        WriteSkill(Path.Combine(Folder, ".import-暫存"), "暫存", "d", "步驟");
        WriteSkill(Path.Combine(Folder, "週報"), "週報", "d", "步驟");

        Assert.Equal(["週報"], (await _store.ListAsync(default)).Select(s => s.Name));
    }

    [Fact]
    public async Task Files_ListTextFiles_AndReportIgnoredOnes()
    {
        var dir = Path.Combine(Folder, "週報");
        WriteSkill(dir, "週報", "d", "步驟");
        WriteFile(Path.Combine(dir, "templates", "週報.md"), "範本");
        WriteFile(Path.Combine(dir, "notes.txt"), "筆記");
        WriteFile(Path.Combine(dir, "scripts", "run.md"), "腳本");
        WriteFile(Path.Combine(dir, "tool.exe"), "不認得");
        WriteFile(Path.Combine(dir, "big.md"), new string('a', 110 * 1024));
        WriteFile(Path.Combine(dir, ".DS_Store"), "隱藏檔不算");

        var doc = await _store.GetAsync("週報", default);

        Assert.Equal(["notes.txt", "templates/週報.md"], doc!.Files);
        Assert.True(doc.Info.HasIgnoredFiles);
    }

    [Fact]
    public async Task HasIgnoredFiles_IsFalse_WhenOnlyTextFilesAndHiddenFiles()
    {
        var dir = Path.Combine(Folder, "乾淨");
        WriteSkill(dir, "乾淨", "d", "步驟");
        WriteFile(Path.Combine(dir, "a.md"), "a");
        WriteFile(Path.Combine(dir, ".DS_Store"), "x");

        var doc = await _store.GetAsync("乾淨", default);

        Assert.False(doc!.Info.HasIgnoredFiles);
        Assert.Equal(["a.md"], doc.Files);
    }

    [Fact]
    public async Task Symlink_InsideSkillFolder_IsNotListed()
    {
        var dir = Path.Combine(Folder, "週報");
        WriteSkill(dir, "週報", "d", "步驟");
        var target = Path.Combine(_root, "outside.md");
        File.WriteAllText(target, "外面的內容");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir, "link.md"), target);
        }
        catch (Exception)
        {
            return; // 沒有建立符號連結的權限（例如 Windows），略過
        }

        var doc = await _store.GetAsync("週報", default);

        Assert.DoesNotContain("link.md", doc!.Files);
        Assert.True(doc.Info.HasIgnoredFiles);
        Assert.Null(await _store.ReadFileAsync("週報", "link.md", default));
    }

    [Fact]
    public async Task ReadFile_ReturnsContent_AndRejectsUnsafeOrUnlistedPaths()
    {
        var dir = Path.Combine(Folder, "週報");
        WriteSkill(dir, "週報", "d", "步驟");
        WriteFile(Path.Combine(dir, "templates", "週報.md"), "範本內容");
        WriteFile(Path.Combine(dir, "scripts", "run.md"), "腳本");
        WriteFile(Path.Combine(dir, "tool.exe"), "不認得");

        Assert.Equal("範本內容", await _store.ReadFileAsync("週報", "templates/週報.md", default));
        Assert.Equal("範本內容", await _store.ReadFileAsync("週報", "templates\\週報.md", default));
        Assert.Null(await _store.ReadFileAsync("週報", "../skills/週報/SKILL.md", default));
        Assert.Null(await _store.ReadFileAsync("週報", "/etc/passwd", default));
        Assert.Null(await _store.ReadFileAsync("週報", "scripts/run.md", default));
        Assert.Null(await _store.ReadFileAsync("週報", "tool.exe", default));
        Assert.Null(await _store.ReadFileAsync("週報", "不存在.md", default));
        Assert.Null(await _store.ReadFileAsync("沒有這個技能", "templates/週報.md", default));
    }

    [Fact]
    public async Task Get_Unknown_ReturnsNull()
    {
        Assert.Null(await _store.GetAsync("沒有", default));
    }

    // ---------- 存檔、改名、刪除 ----------

    [Fact]
    public async Task Save_New_WritesSkillMd_AndListsIt()
    {
        var result = await _store.SaveAsync(Draft("週報", "1. 整理\n2. 寫"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        var text = await File.ReadAllTextAsync(Path.Combine(Folder, "週報", "SKILL.md"));
        Assert.Contains("name: \"週報\"", text);
        var doc = await _store.GetAsync("週報", default);
        Assert.Equal("1. 整理\n2. 寫", doc!.Body);
        Assert.True(doc.Info.Enabled);
    }

    [Fact]
    public async Task Save_InvalidDraft_ReturnsInvalid_AndWritesNothing()
    {
        var result = await _store.SaveAsync(Draft("CON"), default);

        Assert.Equal(SkillSaveOutcome.Invalid, result.Outcome);
        Assert.False(Directory.Exists(Folder));
    }

    [Fact]
    public async Task Save_Modify_KeepsOtherFieldsAndAttachedFiles()
    {
        var dir = Path.Combine(Folder, "週報");
        WriteSkill(dir, "週報", "舊描述", "舊步驟", "allowed-tools: Read");
        WriteFile(Path.Combine(dir, "templates", "週報.md"), "範本");

        var result = await _store.SaveAsync(new SkillDraft("週報", "新描述", "新步驟", "週報"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        var text = await File.ReadAllTextAsync(Path.Combine(dir, "SKILL.md"));
        Assert.Contains("allowed-tools: Read", text);
        Assert.Contains("description: \"新描述\"", text);
        Assert.Equal("範本", await File.ReadAllTextAsync(Path.Combine(dir, "templates", "週報.md")));
        var doc = await _store.GetAsync("週報", default);
        Assert.Equal(["templates/週報.md"], doc!.Files);
        Assert.Equal("新步驟", doc.Body);
    }

    [Fact]
    public async Task Save_Modify_NotFound_WhenOriginalIsMissing()
    {
        var result = await _store.SaveAsync(Draft("新的", original: "舊的"), default);

        Assert.Equal(SkillSaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Save_NewWithSameNameIgnoringCase_IsNameTaken()
    {
        await _store.SaveAsync(Draft("Weekly"), default);

        var result = await _store.SaveAsync(Draft("weekly"), default);

        Assert.Equal(SkillSaveOutcome.NameTaken, result.Outcome);
        Assert.Single(await _store.ListAsync(default));
    }

    [Fact]
    public async Task Save_Rename_MovesFolder_AndDisabledStateFollows()
    {
        await _store.SaveAsync(Draft("週報"), default);
        await _catalog.SetSkillEnabledAsync("週報", false);

        var result = await _store.SaveAsync(Draft("週報2", "新步驟", original: "週報"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(Folder, "週報")));
        Assert.True(File.Exists(Path.Combine(Folder, "週報2", "SKILL.md")));
        Assert.True(_catalog.Current.Skills.IsDisabled("週報2"));
        Assert.False(_catalog.Current.Skills.IsDisabled("週報"));
        var list = await _store.ListAsync(default);
        Assert.False(list.Single().Enabled);
    }

    [Fact]
    public async Task Save_Rename_KeepsEnabledSkillEnabled_EvenIfNameWasStaleDisabled()
    {
        await _catalog.SetSkillEnabledAsync("新名", false);
        await _store.SaveAsync(Draft("舊名"), default);

        await _store.SaveAsync(Draft("新名", original: "舊名"), default);

        Assert.False(_catalog.Current.Skills.IsDisabled("新名"));
    }

    [Fact]
    public async Task Save_CaseOnlyRename_Works()
    {
        await _store.SaveAsync(Draft("abc"), default);

        var result = await _store.SaveAsync(Draft("ABC", original: "abc"), default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        var folders = Directory.EnumerateDirectories(Folder).Select(Path.GetFileName).ToList();
        Assert.Equal(["ABC"], folders);
        Assert.Equal(["ABC"], (await _store.ListAsync(default)).Select(s => s.Name));
    }

    [Fact]
    public async Task Save_RenameToAnotherExistingSkill_IsNameTaken()
    {
        await _store.SaveAsync(Draft("甲"), default);
        await _store.SaveAsync(Draft("乙"), default);

        var result = await _store.SaveAsync(Draft("乙", original: "甲"), default);

        Assert.Equal(SkillSaveOutcome.NameTaken, result.Outcome);
        Assert.True(Directory.Exists(Path.Combine(Folder, "甲")));
    }

    [Fact]
    public async Task Save_WhenEnabledIsFull_SavesDisabled()
    {
        for (var i = 0; i < SkillRules.MaxEnabled; i++)
            Assert.Equal(SkillSaveOutcome.Saved, (await _store.SaveAsync(Draft($"技能{i:D2}"), default)).Outcome);

        var result = await _store.SaveAsync(Draft("第三十一個"), default);

        Assert.Equal(SkillSaveOutcome.SavedDisabled, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.False((await _store.ListAsync(default)).Single(s => s.Name == "第三十一個").Enabled);
    }

    [Fact]
    public async Task Save_WhenASkillWasDisabledAndDeleted_NewSkillWithSameNameIsEnabled()
    {
        await _store.SaveAsync(Draft("週報"), default);
        await _catalog.SetSkillEnabledAsync("週報", false);
        await _store.DeleteAsync("週報", default);

        await _store.SaveAsync(Draft("週報"), default);

        Assert.False(_catalog.Current.Skills.IsDisabled("週報"));
    }

    [Fact]
    public async Task Delete_RemovesFolder_AndMissingIsNoOp()
    {
        await _store.SaveAsync(Draft("週報"), default);

        await _store.DeleteAsync("週報", default);
        await _store.DeleteAsync("不存在", default);

        Assert.False(Directory.Exists(Path.Combine(Folder, "週報")));
        Assert.Empty(await _store.ListAsync(default));
    }

    // ---------- 匯入：資料夾 ----------

    [Fact]
    public async Task ImportFolder_CopiesOnlyTextFiles_AndInstalls()
    {
        var source = Path.Combine(_root, "src", "週報");
        WriteSkill(source, "週報", "每週五寫週報", "1. 整理");
        WriteFile(Path.Combine(source, "templates", "範本.md"), "範本");
        WriteFile(Path.Combine(source, "scripts", "run.py"), "print(1)");
        WriteFile(Path.Combine(source, "image.png"), "png");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.Equal("週報", preview.Draft.Name);
        Assert.Equal(["templates/範本.md"], preview.Files);
        Assert.Contains(preview.Skipped, s => s.Path == "scripts/run.py" && s.Reason == "腳本不會複製");
        Assert.Contains(preview.Skipped, s => s.Path == "image.png" && s.Reason == "不是文字檔");
        Assert.Contains(preview.Warnings, w => w.Contains(ScriptsWarningPart));
        Assert.True(Directory.Exists(preview.StagingPath));

        var result = await _store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        var installed = Path.Combine(Folder, "週報");
        Assert.Equal("範本", await File.ReadAllTextAsync(Path.Combine(installed, "templates", "範本.md")));
        Assert.False(Directory.Exists(Path.Combine(installed, "scripts")));
        Assert.False(File.Exists(Path.Combine(installed, "image.png")));
        Assert.Equal(0, StagingCount());
        Assert.Equal(["週報"], (await _store.ListAsync(default)).Select(s => s.Name));
    }

    [Fact]
    public async Task ImportFolder_OneLevelDown_IsFound()
    {
        var pick = Path.Combine(_root, "pick");
        WriteSkill(Path.Combine(pick, "會議資料夾"), "會議紀錄", "整理會議", "步驟");

        var preview = await _store.PreviewImportAsync(pick, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.Equal("會議紀錄", preview.Draft.Name);
    }

    [Fact]
    public async Task ImportFolder_WithoutName_UsesTheFolderName()
    {
        var source = Path.Combine(_root, "會議紀錄");
        WriteFile(Path.Combine(source, "SKILL.md"), "---\ndescription: d\n---\n步驟");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.Equal("會議紀錄", preview.Draft.Name);
    }

    [Fact]
    public async Task ImportFolder_Symlink_IsSkipped()
    {
        var source = Path.Combine(_root, "src", "週報");
        WriteSkill(source, "週報", "d", "步驟");
        var outside = Path.Combine(_root, "outside.md");
        File.WriteAllText(outside, "外面");
        try
        {
            File.CreateSymbolicLink(Path.Combine(source, "link.md"), outside);
        }
        catch (Exception)
        {
            return; // 沒有建立符號連結的權限，略過
        }

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.DoesNotContain("link.md", preview.Files);
        Assert.Contains(preview.Skipped, s => s.Path == "link.md" && s.Reason.Contains("符號連結"));
        await _store.DiscardImportAsync(preview, default);
    }

    [Fact]
    public async Task ImportFolder_WithoutSkillMd_IsAProblem_AndCleansUp()
    {
        var source = Path.Combine(_root, "空的");
        WriteFile(Path.Combine(source, "notes.md"), "沒有 SKILL.md");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.False(preview.CanInstall);
        Assert.Equal("找不到 SKILL.md，這可能不是技能。", preview.Problem);
        Assert.Null(preview.StagingPath);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportFolder_SeveralSkills_IsAProblem()
    {
        var source = Path.Combine(_root, "多個");
        WriteSkill(Path.Combine(source, "a"), "甲", "d", "步驟");
        WriteSkill(Path.Combine(source, "b"), "乙", "d", "步驟");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.Equal("這裡面有好幾個技能，請一次匯入一個。", preview.Problem);
        Assert.Null(preview.StagingPath);
    }

    [Fact]
    public async Task ImportFolder_TooManyFiles_IsAProblem()
    {
        var source = Path.Combine(_root, "很多檔");
        WriteSkill(source, "很多檔", "d", "步驟");
        for (var i = 0; i < SkillRules.MaxFiles; i++)
            WriteFile(Path.Combine(source, $"f{i:D2}.md"), "x");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("檔案超過", preview.Problem);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportFolder_TotalTooLarge_IsAProblem()
    {
        var source = Path.Combine(_root, "很大");
        WriteSkill(source, "很大", "d", "步驟");
        for (var i = 0; i < 21; i++)
            WriteFile(Path.Combine(source, $"f{i:D2}.md"), new string('a', (int)SkillRules.MaxFileBytes));

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("2 MB", preview.Problem);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportFolder_SkillMdTooBig_IsAProblem()
    {
        var source = Path.Combine(_root, "太大");
        WriteFile(Path.Combine(source, "SKILL.md"), Md("太大", new string('a', 110 * 1024)));

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("SKILL.md 太大", preview.Problem);
    }

    [Fact]
    public async Task ImportFolder_InvalidName_IsAProblem_WithoutStaging()
    {
        var source = Path.Combine(_root, "壞名字");
        WriteFile(Path.Combine(source, "SKILL.md"), SkillFrontmatter.Write("CON", "d", "步驟"));

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.False(preview.CanInstall);
        Assert.Equal(SkillRules.ValidateName("CON"), preview.Problem);
        Assert.Null(preview.StagingPath);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportFolder_ReplacesSameName_WarnsAndInstallReplaces()
    {
        await _store.SaveAsync(Draft("週報", "舊步驟"), default);
        WriteFile(Path.Combine(Folder, "週報", "old.md"), "舊的附件");
        var source = Path.Combine(_root, "src", "週報");
        WriteSkill(source, "週報", "d", "新步驟");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.True(preview.CanInstall);
        Assert.True(preview.ReplacesExisting);
        Assert.Contains(preview.Warnings, w => w.Contains("取代它"));

        var result = await _store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.Saved, result.Outcome);
        Assert.False(File.Exists(Path.Combine(Folder, "週報", "old.md")));
        Assert.Equal("新步驟", (await _store.GetAsync("週報", default))!.Body);
    }

    [Fact]
    public async Task ImportFolder_AllowedToolsField_WarnsButIsKept()
    {
        var source = Path.Combine(_root, "src", "工具");
        WriteSkill(source, "工具", "d", "步驟", "allowed-tools: Bash");

        var preview = await _store.PreviewImportAsync(source, default);

        Assert.Contains(preview.Warnings, w => w.Contains(FieldsWarningPart));
        Assert.True(preview.Draft.Name == "工具");
        await _store.InstallAsync(preview, default);
        Assert.Contains("allowed-tools: Bash", await File.ReadAllTextAsync(Path.Combine(Folder, "工具", "SKILL.md")));
    }

    [Fact]
    public async Task ImportFolder_OverEnabledLimit_InstallsDisabled()
    {
        for (var i = 0; i < SkillRules.MaxEnabled; i++)
            await _store.SaveAsync(Draft($"技能{i:D2}"), default);
        var source = Path.Combine(_root, "src", "新的");
        WriteSkill(source, "新的", "d", "步驟");

        var preview = await _store.PreviewImportAsync(source, default);
        var result = await _store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.SavedDisabled, result.Outcome);
        Assert.False((await _store.ListAsync(default)).Single(s => s.Name == "新的").Enabled);
    }

    [Fact]
    public async Task ImportFolder_NotFolderOrZip_IsAProblem()
    {
        var preview = await _store.PreviewImportAsync(Path.Combine(_root, "不存在.txt"), default);

        Assert.Equal("只能匯入資料夾或 zip 檔。", preview.Problem);
        Assert.False(preview.CanInstall);
    }

    [Fact]
    public async Task Discard_RemovesStaging()
    {
        var source = Path.Combine(_root, "src", "丟掉");
        WriteSkill(source, "丟掉", "d", "步驟");
        var preview = await _store.PreviewImportAsync(source, default);
        Assert.Equal(1, StagingCount());

        await _store.DiscardImportAsync(preview, default);

        Assert.Equal(0, StagingCount());
        Assert.Empty(await _store.ListAsync(default));
    }

    [Fact]
    public async Task Install_AfterDiscard_IsInvalid()
    {
        var source = Path.Combine(_root, "src", "丟掉");
        WriteSkill(source, "丟掉", "d", "步驟");
        var preview = await _store.PreviewImportAsync(source, default);
        await _store.DiscardImportAsync(preview, default);

        var result = await _store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Install_NotCanInstall_IsInvalid()
    {
        var preview = await _store.PreviewImportAsync(Path.Combine(_root, "不存在"), default);

        var result = await _store.InstallAsync(preview, default);

        Assert.Equal(SkillSaveOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void CleanupStaleImports_RemovesLeftoverStaging_AndKeepsSkills()
    {
        WriteSkill(Path.Combine(Folder, "週報"), "週報", "d", "步驟");
        Directory.CreateDirectory(Path.Combine(Folder, ".import-abc"));
        Directory.CreateDirectory(Path.Combine(Folder, ".rename-def"));

        _store.CleanupStaleImports();

        Assert.False(Directory.Exists(Path.Combine(Folder, ".import-abc")));
        Assert.False(Directory.Exists(Path.Combine(Folder, ".rename-def")));
        Assert.True(File.Exists(Path.Combine(Folder, "週報", "SKILL.md")));
    }

    // ---------- 匯入：zip ----------

    [Fact]
    public async Task ImportZip_RootSkill_Installs()
    {
        var zip = CreateZip("週報.zip", a =>
        {
            AddText(a, "SKILL.md", Md("週報"));
            AddText(a, "templates/範本.md", "範本");
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.Equal(["templates/範本.md"], preview.Files);
        Assert.Equal(SkillSaveOutcome.Saved, (await _store.InstallAsync(preview, default)).Outcome);
        Assert.True(File.Exists(Path.Combine(Folder, "週報", "templates", "範本.md")));
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportZip_WrappedFolder_FallsBackToFolderName()
    {
        var zip = CreateZip("包.zip", a => AddText(a, "會議/SKILL.md", "---\ndescription: d\n---\n步驟"));

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.Equal("會議", preview.Draft.Name);
    }

    [Fact]
    public async Task ImportZip_WrappedFolder_ReadsSkillInsideIt()
    {
        var zip = CreateZip("包.zip", a =>
        {
            AddText(a, "週報資料夾/SKILL.md", Md("週報"));
            AddText(a, "週報資料夾/templates/範本.md", "範本");
            AddText(a, "__MACOSX/週報資料夾/._SKILL.md", "junk");
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.Equal("週報", preview.Draft.Name);
        Assert.Equal(["templates/範本.md"], preview.Files);
    }

    [Fact]
    public async Task ImportZip_DotDot_IsRejected_AndNothingIsWritten()
    {
        var zip = CreateZip("壞.zip", a =>
        {
            AddText(a, "SKILL.md", Md("壞"));
            AddText(a, "../evil.md", "逃出去");
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("不安全", preview.Problem);
        Assert.Null(preview.StagingPath);
        Assert.Equal(0, StagingCount());
        Assert.False(File.Exists(Path.Combine(_root, "evil.md")));
    }

    [Fact]
    public async Task ImportZip_AbsolutePath_IsRejected()
    {
        var zip = CreateZip("絕對.zip", a =>
        {
            AddText(a, "SKILL.md", Md("絕對"));
            AddText(a, "/etc/evil.md", "x");
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("不安全", preview.Problem);
    }

    [Fact]
    public async Task ImportZip_SymbolicLinkEntry_IsRejected()
    {
        var zip = CreateZip("連結.zip", a =>
        {
            AddText(a, "SKILL.md", Md("連結"));
            var link = a.CreateEntry("link.md");
            link.ExternalAttributes = 0xA1FF << 16; // unix 模式 lrwxrwxrwx
            using var stream = link.Open();
            stream.Write(Encoding.UTF8.GetBytes("/etc/passwd"));
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("符號連結", preview.Problem);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportZip_FileTooLarge_IsRejected()
    {
        var path = Path.Combine(_root, "太大.zip");
        File.WriteAllBytes(path, new byte[SkillRules.MaxZipBytes + 1]);

        var preview = await _store.PreviewImportAsync(path, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("壓縮檔太大", preview.Problem);
    }

    [Fact]
    public async Task ImportZip_DecompressedTooLarge_IsRejected_ByActualBytes()
    {
        // 解壓後的內容很大但壓縮後很小：不能只相信宣告的大小，要以實際讀到的位元組計算
        var zip = CreateZip("炸彈.zip", a =>
        {
            AddText(a, "SKILL.md", Md("炸彈"));
            AddBytes(a, "big.md", new byte[SkillRules.MaxSkillBytes + 1]);
        });
        Assert.True(new FileInfo(zip).Length < SkillRules.MaxZipBytes);

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.False(preview.CanInstall);
        Assert.Contains("解壓後太大", preview.Problem);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportZip_AttachmentOverFileLimit_IsSkipped()
    {
        var zip = CreateZip("大附件.zip", a =>
        {
            AddText(a, "SKILL.md", Md("大附件"));
            AddText(a, "big.md", new string('a', (int)SkillRules.MaxFileBytes + 1));
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.True(preview.CanInstall, preview.Problem);
        Assert.DoesNotContain("big.md", preview.Files);
        Assert.Contains(preview.Skipped, s => s.Path == "big.md" && s.Reason == "檔案太大");
    }

    [Fact]
    public async Task ImportZip_SeveralSkills_IsAProblem()
    {
        var zip = CreateZip("多個.zip", a =>
        {
            AddText(a, "甲/SKILL.md", Md("甲"));
            AddText(a, "乙/SKILL.md", Md("乙"));
        });

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.Equal("這裡面有好幾個技能，請一次匯入一個。", preview.Problem);
    }

    [Fact]
    public async Task ImportZip_NoSkill_IsAProblem()
    {
        var zip = CreateZip("沒有.zip", a => AddText(a, "readme.md", "x"));

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.Equal("找不到 SKILL.md，這可能不是技能。", preview.Problem);
    }

    [Fact]
    public async Task ImportZip_ScriptsAndBinaries_AreSkipped_WarnedAndNotInstalled()
    {
        var zip = CreateZip("完整.zip", a =>
        {
            AddText(a, "SKILL.md", Md("完整"));
            AddText(a, "scripts/run.md", "腳本");
            AddBytes(a, "tool.exe", [1, 2, 3]);
        });

        var preview = await _store.PreviewImportAsync(zip, default);
        Assert.True(preview.CanInstall, preview.Problem);

        Assert.Contains(preview.Skipped, s => s.Path == "scripts/run.md" && s.Reason == "腳本不會複製");
        Assert.Contains(preview.Skipped, s => s.Path == "tool.exe" && s.Reason == "不是文字檔");
        Assert.Contains(preview.Warnings, w => w.Contains(ScriptsWarningPart));

        await _store.InstallAsync(preview, default);
        Assert.False(Directory.Exists(Path.Combine(Folder, "完整", "scripts")));
        Assert.False(File.Exists(Path.Combine(Folder, "完整", "tool.exe")));
    }

    [Fact]
    public async Task ImportZip_CorruptFile_IsAProblem_AndCleansUp()
    {
        var path = Path.Combine(_root, "壞掉.zip");
        File.WriteAllText(path, "這不是 zip");

        var preview = await _store.PreviewImportAsync(path, default);

        Assert.False(preview.CanInstall);
        Assert.NotNull(preview.Problem);
        Assert.Equal(0, StagingCount());
    }

    [Fact]
    public async Task ImportZip_ReplacesSameName_Warns()
    {
        await _store.SaveAsync(Draft("週報"), default);
        var zip = CreateZip("週報.zip", a => AddText(a, "SKILL.md", Md("週報", "新的")));

        var preview = await _store.PreviewImportAsync(zip, default);

        Assert.True(preview.ReplacesExisting);
        Assert.Contains(preview.Warnings, w => w.Contains("取代它"));
    }
}
