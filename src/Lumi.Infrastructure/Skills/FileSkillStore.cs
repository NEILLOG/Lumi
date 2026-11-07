using System.IO.Compression;
using System.Text;
using Lumi.Core.IO;
using Lumi.Core.Models;
using Lumi.Core.Skills;
using Microsoft.Extensions.Logging;

namespace Lumi.Infrastructure.Skills;

/// <summary>
/// <see cref="ISkillStore"/> 的檔案版本（M29 5.4）：一個技能一個資料夾 <c>{Folder}/{名稱}/SKILL.md</c>，附帶的文字檔放在同一個資料夾裡。
/// 清單有快取；會改動資料夾的操作在鎖內完成，之後才改啟用狀態（<see cref="IModelCatalog"/>）並發出 <see cref="Changed"/>。
/// 以 <c>.</c> 開頭的資料夾是 Lumi 自己的暫存（匯入、改名），掃描時一律略過。
/// </summary>
public sealed class FileSkillStore : ISkillStore
{
    private const string SkillFile = SkillRules.SkillFileName;
    private const string ImportPrefix = ".import-";
    private const string RenamePrefix = ".rename-";
    private const string StagedSkillFolder = "skill";
    private const string ScriptsFolder = "scripts";

    /// <summary>匯入時會讓使用者覺得「權限或模型設定有效」的欄位（D6：保留但不生效）。</summary>
    private static readonly string[] FieldsWarningKeys = ["allowed-tools", "model"];

    // 以下文字與 Strings.resx 的 Skills_* 相同。Infrastructure 看不到 App 的資源，所以在這裡重複一份（改動時兩邊都要改）。
    private const string ScriptsWarning = "這個技能附帶腳本或程式，Lumi 不會執行，也不會複製；技能可能無法完整運作。";
    private const string FieldsWarning = "這個技能要求的工具權限或模型設定不會生效，工具照常需要你確認。";
    private const string NoSkillFileProblem = "找不到 SKILL.md，這可能不是技能。";
    private const string ManySkillsProblem = "這裡面有好幾個技能，請一次匯入一個。";
    private const string OnlyFolderOrZipProblem = "只能匯入資料夾或 zip 檔。";
    private const string DisabledLimitFormat = "已經有 {0} 個啟用中的技能，這個技能先存成停用。";

    private const string ReasonScript = "腳本不會複製";
    private const string ReasonNotText = "不是文字檔";
    private const string ReasonTooBig = "檔案太大";
    private const string ReasonLink = "符號連結，不會複製";
    private const string DuplicateNameProblem = "和另一個技能同名。";
    private const string ReadFailedProblem = "讀取失敗，檔案可能已損壞或無法讀取。";
    private const string StaleImportProblem = "這份匯入已經失效，請重新匯入。";

    private readonly string _folder;
    private readonly IModelCatalog _catalog;
    private readonly ILogger<FileSkillStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>掃描結果。只整份替換、不原地修改；<c>null</c> 表示下次要重新掃描。只在 <see cref="_lock"/> 內讀寫。</summary>
    private List<Entry>? _cache;

    public FileSkillStore(string folder, IModelCatalog catalog, ILogger<FileSkillStore> logger)
    {
        _folder = folder;
        _catalog = catalog;
        _logger = logger;
        _catalog.Changed += OnCatalogChanged;
    }

    public event EventHandler? Changed;

    public string Folder => _folder;

    public async Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken ct)
    {
        var entries = await SnapshotAsync(ct).ConfigureAwait(false);
        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(ToInfo).ToList();
    }

    public async Task<SkillDocument?> GetAsync(string name, CancellationToken ct)
    {
        var entries = await SnapshotAsync(ct).ConfigureAwait(false);
        var entry = Find(entries, name);
        return entry is null ? null : new SkillDocument(ToInfo(entry), entry.Body, entry.Files);
    }

    public async Task<string?> ReadFileAsync(string name, string relativePath, CancellationToken ct)
    {
        if (!SkillRules.TryNormalizeRelativePath(relativePath, out var path))
            return null;

        var entries = await SnapshotAsync(ct).ConfigureAwait(false);
        var entry = Find(entries, name);
        if (entry is null || !entry.Files.Contains(path, StringComparer.Ordinal))
            return null;

        var full = Path.Combine(entry.FullPath, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || IsLink(info) || info.Length > SkillRules.MaxFileBytes)
                return null;
            return await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "讀取技能的附帶檔案失敗");
            return null;
        }
    }

    public async Task<SkillSaveResult> SaveAsync(SkillDraft draft, CancellationToken ct)
    {
        if (SkillRules.Validate(draft) is { } problem)
            return new SkillSaveResult(SkillSaveOutcome.Invalid, problem);

        var name = SkillRules.NormalizeName(draft.Name);
        var description = SkillRules.NormalizeDescription(draft.Description);
        var body = SkillRules.NormalizeBody(draft.Body);
        var original = draft.OriginalName is null ? null : SkillRules.NormalizeName(draft.OriginalName);

        Mutation mutation;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            mutation = original is null
                ? await CreateLockedAsync(name, description, body, ct).ConfigureAwait(false)
                : await UpdateLockedAsync(original, name, description, body, ct).ConfigureAwait(false);
        }
        finally
        {
            _cache = null;
            _lock.Release();
        }

        await ApplyAfterMutationAsync(mutation, name, ct).ConfigureAwait(false);
        if (mutation.Result.Succeeded)
            Changed?.Invoke(this, EventArgs.Empty);
        return mutation.Result;
    }

    public async Task DeleteAsync(string name, CancellationToken ct)
    {
        var normalized = SkillRules.NormalizeName(name);
        bool removed;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = Find(ScanLocked(), normalized);
            removed = entry is not null;
            if (entry is not null)
                Directory.Delete(entry.FullPath, recursive: true);
        }
        finally
        {
            _cache = null;
            _lock.Release();
        }

        if (!removed)
            return;

        // 刪掉的技能若還留著停用紀錄，同名的新技能會莫名其妙被停用，所以一併清掉
        await ApplyEnabledAsync(normalized, enabled: true, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SkillImportPreview> PreviewImportAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Failed(path, OnlyFolderOrZipProblem);
        if (Directory.Exists(path))
            return await PreviewFolderAsync(path, ct).ConfigureAwait(false);
        if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return await PreviewZipAsync(path, ct).ConfigureAwait(false);
        return Failed(path, OnlyFolderOrZipProblem);
    }

    public async Task<SkillSaveResult> InstallAsync(SkillImportPreview preview, CancellationToken ct)
    {
        if (!preview.CanInstall || !IsStagingPath(preview.StagingPath))
            return new SkillSaveResult(SkillSaveOutcome.Invalid, preview.Problem ?? StaleImportProblem);
        if (SkillRules.Validate(preview.Draft) is { } problem)
            return new SkillSaveResult(SkillSaveOutcome.Invalid, problem);

        var name = SkillRules.NormalizeName(preview.Draft.Name);
        var description = SkillRules.NormalizeDescription(preview.Draft.Description);
        var body = SkillRules.NormalizeBody(preview.Draft.Body);
        var staged = Path.Combine(preview.StagingPath!, StagedSkillFolder);
        var stagedSkill = Path.Combine(staged, SkillFile);
        if (!File.Exists(stagedSkill))
            return new SkillSaveResult(SkillSaveOutcome.Invalid, StaleImportProblem);

        // 其他欄位（allowed-tools 之類）照原樣保留（D6）
        var otherLines = SkillFrontmatter.Parse(await File.ReadAllTextAsync(stagedSkill, Encoding.UTF8, ct).ConfigureAwait(false)).OtherLines;

        Mutation mutation;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            mutation = await InstallLockedAsync(preview.StagingPath!, staged, name, description, body, otherLines, ct).ConfigureAwait(false);
        }
        finally
        {
            _cache = null;
            _lock.Release();
        }

        await ApplyAfterMutationAsync(mutation, name, ct).ConfigureAwait(false);
        if (mutation.Result.Succeeded)
            Changed?.Invoke(this, EventArgs.Empty);
        return mutation.Result;
    }

    public Task DiscardImportAsync(SkillImportPreview preview, CancellationToken ct)
    {
        if (IsStagingPath(preview.StagingPath))
            DeleteQuietly(preview.StagingPath);
        return Task.CompletedTask;
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _cache = ScanLocked();
        }
        finally
        {
            _lock.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>刪掉上次沒有完成的匯入與改名暫存（<c>.import-*</c>、<c>.rename-*</c>）。啟動時呼叫一次。</summary>
    public void CleanupStaleImports()
    {
        if (!Directory.Exists(_folder))
            return;

        _lock.Wait();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_folder).ToList())
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(ImportPrefix, StringComparison.Ordinal) || name.StartsWith(RenamePrefix, StringComparison.Ordinal))
                    DeleteQuietly(dir);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------- 掃描與快取 ----------

    /// <summary>掃描結果的一個技能（不含啟用狀態，啟用狀態在讀取時才從設定算）。</summary>
    private sealed record Entry(
        string FolderName,
        string FullPath,
        string Name,
        string Description,
        string Body,
        IReadOnlyList<string> OtherLines,
        IReadOnlyList<string> Files,
        bool HasIgnoredFiles,
        string? Problem);

    private async Task<List<Entry>> SnapshotAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return _cache ??= ScanLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>掃描整個技能資料夾（呼叫端持有 <see cref="_lock"/>）。依資料夾名稱 Ordinal 排序，同名時第二個起標成錯誤。</summary>
    private List<Entry> ScanLocked()
    {
        var entries = new List<Entry>();
        if (!Directory.Exists(_folder))
            return entries;

        List<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(_folder).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "列出技能資料夾失敗");
            return entries;
        }

        foreach (var dir in dirs)
        {
            var folderName = Path.GetFileName(dir);
            if (folderName.StartsWith('.'))
                continue;
            entries.Add(ScanFolder(dir, folderName));
        }

        var names = new HashSet<string>(SkillRules.NameComparer);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!names.Add(entry.Name) && entry.Problem is null)
                entries[i] = entry with { Problem = DuplicateNameProblem };
        }
        return entries;
    }

    private Entry ScanFolder(string dir, string folderName)
    {
        try
        {
            var items = WalkFolder(dir);

            var attachable = new List<string>();
            var ignored = false;
            foreach (var item in items)
            {
                if (item.Rel == SkillFile)
                    continue;
                if (!item.IsLink && IsAttachable(item.Rel, item.Size))
                    attachable.Add(item.Rel);
                else
                    ignored = true;
            }
            attachable.Sort(StringComparer.Ordinal);

            var name = folderName;
            var description = "";
            var body = "";
            IReadOnlyList<string> otherLines = [];
            string? problem;
            var skill = items.FirstOrDefault(i => i.Rel == SkillFile);
            if (skill is null)
            {
                problem = NoSkillFileProblem;
            }
            else if (skill.IsLink)
            {
                problem = "SKILL.md 是符號連結，Lumi 不讀取。";
            }
            else if (skill.Size > SkillRules.MaxFileBytes)
            {
                problem = "SKILL.md 太大（超過 100 KB）。";
            }
            else
            {
                var markdown = SkillFrontmatter.Parse(File.ReadAllText(skill.FullPath, Encoding.UTF8));
                if (!string.IsNullOrWhiteSpace(markdown.Name))
                    name = SkillRules.NormalizeName(markdown.Name);
                description = SkillRules.NormalizeDescription(markdown.Description);
                body = markdown.Body;
                otherLines = markdown.OtherLines;
                problem = ValidateLoaded(name, description, body);
            }

            return new Entry(folderName, dir, name, description, body, otherLines, attachable, ignored, problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "讀取技能資料夾失敗");
            return new Entry(folderName, dir, folderName, "", "", [], [], false, "讀取失敗，這個資料夾沒有載入。");
        }
    }

    /// <summary>已存在的技能讀進來時的檢查：比存檔寬鬆，內文是空的也能載入（存檔時才要求）。</summary>
    private static string? ValidateLoaded(string name, string description, string body)
    {
        if (SkillRules.ValidateName(name) is { } nameProblem)
            return nameProblem;
        if (description.Length == 0)
            return "缺少描述。";
        if (SkillRules.ValidateDescription(description) is { } descriptionProblem)
            return descriptionProblem;
        if (body.Length > 0)
            return SkillRules.ValidateBody(body);
        return null;
    }

    private static bool IsAttachable(string rel, long size) =>
        SkillRules.IsTextFile(rel) && !IsScriptPath(rel) && size <= SkillRules.MaxFileBytes;

    /// <summary>位於 <c>scripts/</c> 底下（第一個資料夾名稱不分大小寫是 scripts）。</summary>
    private static bool IsScriptPath(string rel) =>
        rel.Contains('/') && string.Equals(rel.Split('/')[0], ScriptsFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>資料夾裡的一個檔案或符號連結（不含以 <c>.</c> 開頭的項目，那些不算）。Rel 用 <c>/</c> 分隔。</summary>
    private sealed record FolderItem(string Rel, string FullPath, long Size, bool IsLink);

    private static List<FolderItem> WalkFolder(string root)
    {
        var items = new List<FolderItem>();
        WalkFolder(root, "", items);
        return items;
    }

    private static void WalkFolder(string root, string relDir, List<FolderItem> items)
    {
        var dir = relDir.Length == 0 ? root : Path.Combine(root, relDir.Replace('/', Path.DirectorySeparatorChar));
        foreach (var path in Directory.EnumerateFileSystemEntries(dir).ToList())
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.'))
                continue;

            var rel = relDir.Length == 0 ? name : relDir + "/" + name;
            var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if (IsLink(info))
            {
                items.Add(new FolderItem(rel, path, 0, IsLink: true));
                continue;
            }
            if (info is DirectoryInfo)
            {
                WalkFolder(root, rel, items);
                continue;
            }
            items.Add(new FolderItem(rel, path, ((FileInfo)info).Length, IsLink: false));
        }
    }

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private SkillInfo ToInfo(Entry entry) => new(
        entry.Name, entry.Description, !_catalog.Current.Skills.IsDisabled(entry.Name), entry.HasIgnoredFiles, entry.Problem);

    private static Entry? Find(IEnumerable<Entry> entries, string name)
    {
        var normalized = SkillRules.NormalizeName(name);
        return entries.FirstOrDefault(e => SkillRules.NameComparer.Equals(e.Name, normalized));
    }

    private static bool NameEquals(string a, string b) => SkillRules.NameComparer.Equals(a, b);

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path);

    private int UsableCount(IEnumerable<Entry> entries) =>
        entries.Count(e => e.Problem is null && !_catalog.Current.Skills.IsDisabled(e.Name));

    // ---------- 新增、修改、改名 ----------

    /// <summary>鎖內的改動結果：要在鎖外做的啟用狀態與改名（它們會發出設定變更通知，不能在鎖內進行）。</summary>
    private sealed record Mutation(SkillSaveResult Result, string? RenamedFrom = null, bool? Enable = null);

    private async Task<Mutation> CreateLockedAsync(string name, string description, string body, CancellationToken ct)
    {
        var entries = ScanLocked();
        var target = Path.Combine(_folder, name);
        if (entries.Any(e => NameEquals(e.Name, name)) || PathExists(target))
            return new Mutation(new SkillSaveResult(SkillSaveOutcome.NameTaken, $"已經有名為「{name}」的技能。"));

        var enabled = UsableCount(entries) < SkillRules.MaxEnabled;
        Directory.CreateDirectory(target);
        await WriteSkillFileAsync(target, name, description, body, [], ct).ConfigureAwait(false);
        return new Mutation(SaveResult(enabled), Enable: enabled);
    }

    private async Task<Mutation> UpdateLockedAsync(string original, string name, string description, string body, CancellationToken ct)
    {
        var entries = ScanLocked();
        var existing = Find(entries, original);
        if (existing is null)
            return new Mutation(new SkillSaveResult(SkillSaveOutcome.NotFound, $"找不到技能「{original}」。"));

        var renaming = !string.Equals(original, name, StringComparison.Ordinal);
        if (!renaming)
        {
            // 只改 SKILL.md 的名稱、描述與內文；附帶檔案與其他欄位保留
            await WriteSkillFileAsync(existing.FullPath, name, description, body, existing.OtherLines, ct).ConfigureAwait(false);
            return new Mutation(new SkillSaveResult(SkillSaveOutcome.Saved));
        }

        if (entries.Any(e => !ReferenceEquals(e, existing) && NameEquals(e.Name, name)))
            return new Mutation(new SkillSaveResult(SkillSaveOutcome.NameTaken, $"已經有名為「{name}」的技能。"));

        // 停用狀態在改名前讀，改名後跟著搬到新名稱（呼叫端做）
        var wasDisabled = _catalog.Current.Skills.IsDisabled(original);
        var currentDir = existing.FullPath;
        var folderName = Path.GetFileName(existing.FullPath);
        if (!string.Equals(folderName, name, StringComparison.Ordinal))
        {
            var target = Path.Combine(_folder, name);
            if (NameEquals(folderName, name))
            {
                // 只差大小寫（abc → ABC）：在不分大小寫的檔案系統上不能直接搬，先搬到暫時名稱
                var temp = Path.Combine(_folder, RenamePrefix + Guid.NewGuid().ToString("N"));
                Directory.Move(existing.FullPath, temp);
                Directory.Move(temp, target);
            }
            else
            {
                if (PathExists(target))
                    return new Mutation(new SkillSaveResult(SkillSaveOutcome.NameTaken, $"已經有名為「{name}」的技能。"));
                Directory.Move(existing.FullPath, target);
            }
            currentDir = target;
        }

        await WriteSkillFileAsync(currentDir, name, description, body, existing.OtherLines, ct).ConfigureAwait(false);
        return new Mutation(new SkillSaveResult(SkillSaveOutcome.Saved), RenamedFrom: original, Enable: !wasDisabled);
    }

    /// <summary>安裝匯入的技能（鎖內）：取代同名的舊資料夾，搬入暫存區的技能，SKILL.md 重新寫出，最後刪掉暫存區。</summary>
    private async Task<Mutation> InstallLockedAsync(
        string stagingRoot, string staged, string name, string description, string body, IReadOnlyList<string> otherLines, CancellationToken ct)
    {
        var entries = ScanLocked();
        var existing = Find(entries, name);
        var target = Path.Combine(_folder, name);
        if (existing is null && PathExists(target))
            return new Mutation(new SkillSaveResult(SkillSaveOutcome.NameTaken, $"已經有名為「{name}」的技能。"));

        var usable = UsableCount(entries.Where(e => !ReferenceEquals(e, existing)));
        if (existing is not null)
            Directory.Delete(existing.FullPath, recursive: true);
        if (PathExists(target))
            return new Mutation(new SkillSaveResult(SkillSaveOutcome.NameTaken, $"已經有名為「{name}」的技能。"));

        Directory.Move(staged, target);
        await WriteSkillFileAsync(target, name, description, body, otherLines, ct).ConfigureAwait(false);
        DeleteQuietly(stagingRoot);

        var enabled = usable < SkillRules.MaxEnabled;
        return new Mutation(SaveResult(enabled), Enable: enabled);
    }

    private static SkillSaveResult SaveResult(bool enabled) => enabled
        ? new SkillSaveResult(SkillSaveOutcome.Saved)
        : new SkillSaveResult(SkillSaveOutcome.SavedDisabled, string.Format(DisabledLimitFormat, SkillRules.MaxEnabled));

    private static Task WriteSkillFileAsync(string dir, string name, string description, string body, IReadOnlyList<string> otherLines, CancellationToken ct)
    {
        var text = SkillFrontmatter.Write(name, description, body, otherLines);
        return AtomicFile.WriteAllBytesAsync(Path.Combine(dir, SkillFile), Encoding.UTF8.GetBytes(text), ct);
    }

    private async Task ApplyAfterMutationAsync(Mutation mutation, string name, CancellationToken ct)
    {
        if (mutation.RenamedFrom is { } oldName)
            await _catalog.RenameSkillAsync(oldName, name, ct).ConfigureAwait(false);
        if (mutation.Enable is { } enabled)
            await ApplyEnabledAsync(name, enabled, ct).ConfigureAwait(false);
    }

    /// <summary>已經是想要的狀態就不寫設定檔。</summary>
    private async Task ApplyEnabledAsync(string name, bool enabled, CancellationToken ct)
    {
        if (_catalog.Current.Skills.IsDisabled(name) == !enabled)
            return;
        await _catalog.SetSkillEnabledAsync(name, enabled, ct).ConfigureAwait(false);
    }

    // ---------- 匯入 ----------

    /// <summary>來源的一個檔案。<see cref="Open"/> 只在需要讀取時呼叫（zip 的串流要在呼叫端還開著時使用）。</summary>
    private sealed record SourceItem(string Rel, long Size, Func<Stream> Open);

    /// <summary>檢查不通過：訊息可以直接顯示。只在匯入流程內部使用，不會跑出 store。</summary>
    private sealed class ImportRejectedException(string message) : Exception(message);

    /// <summary>累計實際讀到的位元組，超過 <see cref="SkillRules.MaxSkillBytes"/> 就停止（防 zip 炸彈；不信任宣告大小）。</summary>
    private sealed class ImportBudget
    {
        private long _total;

        public void Add(long bytes)
        {
            _total += bytes;
            if (_total > SkillRules.MaxSkillBytes)
                throw new ImportRejectedException("解壓後太大（超過 2 MB），不能匯入。");
        }
    }

    private async Task<SkillImportPreview> PreviewFolderAsync(string path, CancellationToken ct)
    {
        List<FolderItem> items;
        try
        {
            items = WalkFolder(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "讀取要匯入的資料夾失敗");
            return Failed(path, ReadFailedProblem);
        }

        var skipped = new List<SkippedFile>();
        var sources = new List<SourceItem>();
        foreach (var item in items)
        {
            if (item.IsLink)
            {
                skipped.Add(new SkippedFile(item.Rel, ReasonLink));
                continue;
            }
            var fullPath = item.FullPath;
            sources.Add(new SourceItem(item.Rel, item.Size, () => File.OpenRead(fullPath)));
        }

        var sourceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return await StageAsync(path, sourceName, sources, skipped, isZip: false, ct).ConfigureAwait(false);
    }

    private async Task<SkillImportPreview> PreviewZipAsync(string path, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length > SkillRules.MaxZipBytes)
                return Failed(path, "壓縮檔太大（超過 10 MB），不能匯入。");

            using var archive = ZipFile.OpenRead(path);
            var sources = ZipSources(archive);
            var sourceName = Path.GetFileNameWithoutExtension(path);
            return await StageAsync(path, sourceName, sources, [], isZip: true, ct).ConfigureAwait(false);
        }
        catch (ImportRejectedException ex)
        {
            return Failed(path, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "讀取要匯入的 zip 失敗");
            return Failed(path, ReadFailedProblem);
        }
    }

    /// <summary>zip 的第一關檢查：路徑、符號連結、檔案數（不解壓）。資料夾與目錄項目不列入。</summary>
    private static List<SourceItem> ZipSources(ZipArchive archive)
    {
        var sources = new List<SourceItem>();
        foreach (var entry in archive.Entries)
        {
            var raw = entry.FullName.Replace('\\', '/');
            if (raw.EndsWith('/'))
                continue;

            if (raw.StartsWith('/') || Path.IsPathRooted(raw) || raw.Contains(':'))
                throw new ImportRejectedException("壓縮檔裡有不安全的路徑（絕對路徑），不能匯入。");
            var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
            if (parts.Count == 0 || parts.Contains(".."))
                throw new ImportRejectedException("壓縮檔裡有不安全的路徑（跳出資料夾），不能匯入。");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new ImportRejectedException("壓縮檔裡有符號連結，不能匯入。");

            var target = entry;
            sources.Add(new SourceItem(string.Join('/', parts), target.Length, () => target.Open()));
        }
        return sources;
    }

    /// <summary>
    /// 匯入的共用流程：找 SKILL.md、解壓（或複製）到暫存區、只保留文字檔、解析與檢查。
    /// 有問題時刪掉暫存區，<see cref="SkillImportPreview.StagingPath"/> 回 null。
    /// </summary>
    private async Task<SkillImportPreview> StageAsync(
        string sourcePath, string sourceName, IReadOnlyList<SourceItem> items, List<SkippedFile> skipped, bool isZip, CancellationToken ct)
    {
        var root = Path.Combine(_folder, ImportPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            var (prefix, fallbackName) = FindSkillFolder(items, sourceName);
            if (items.Count > SkillRules.MaxFiles)
                throw new ImportRejectedException($"技能裡的檔案超過 {SkillRules.MaxFiles} 個，不能匯入。");
            if (!isZip && items.Sum(i => i.Size) > SkillRules.MaxSkillBytes)
                throw new ImportRejectedException("技能超過 2 MB，不能匯入。");

            var skillDir = Path.Combine(root, StagedSkillFolder);
            Directory.CreateDirectory(skillDir);
            var budget = isZip ? new ImportBudget() : null;
            var copied = new List<string>();

            foreach (var item in items.OrderBy(i => i.Rel, StringComparer.Ordinal))
            {
                if (!item.Rel.StartsWith(prefix, StringComparison.Ordinal))
                {
                    // zip 要讀完每個項目才能算實際大小；資料夾來源不讀不用的檔案
                    if (isZip)
                        await CopyItemAsync(item, Stream.Null, budget, ct).ConfigureAwait(false);
                    continue;
                }

                var rel = item.Rel[prefix.Length..];
                var reason = SkipReason(rel);
                if (reason is null && !isZip && item.Size > SkillRules.MaxFileBytes)
                    reason = ReasonTooBig;
                if (reason is not null)
                {
                    if (isZip)
                        await CopyItemAsync(item, Stream.Null, budget, ct).ConfigureAwait(false);
                    skipped.Add(new SkippedFile(rel, reason));
                    continue;
                }

                var dest = Path.Combine(skillDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                long written;
                await using (var destination = File.Create(dest))
                {
                    written = await CopyItemAsync(item, destination, budget, ct).ConfigureAwait(false);
                }

                if (written > SkillRules.MaxFileBytes)
                {
                    File.Delete(dest);
                    if (rel == SkillFile)
                        throw new ImportRejectedException("SKILL.md 太大（超過 100 KB），不能匯入。");
                    skipped.Add(new SkippedFile(rel, ReasonTooBig));
                    continue;
                }
                copied.Add(rel);
            }

            var stagedSkill = Path.Combine(skillDir, SkillFile);
            if (!File.Exists(stagedSkill))
                throw new ImportRejectedException("SKILL.md 太大（超過 100 KB），不能匯入。");

            var markdown = SkillFrontmatter.Parse(await File.ReadAllTextAsync(stagedSkill, Encoding.UTF8, ct).ConfigureAwait(false));
            var name = SkillRules.NormalizeName(markdown.Name);
            if (name.Length == 0)
                name = fallbackName;
            var draft = new SkillDraft(name, SkillRules.NormalizeDescription(markdown.Description), markdown.Body);

            var existing = await SnapshotAsync(ct).ConfigureAwait(false);
            var replaces = existing.Any(e => NameEquals(e.Name, name));
            var warnings = BuildWarnings(skipped, markdown.OtherKeys, replaces, name);
            var sortedSkipped = skipped.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();
            var files = copied.Where(f => f != SkillFile).OrderBy(f => f, StringComparer.Ordinal).ToList();

            if (SkillRules.Validate(draft) is { } problem)
            {
                DeleteQuietly(root);
                return new SkillImportPreview(sourcePath, draft, files, sortedSkipped, warnings, replaces, problem, null);
            }
            return new SkillImportPreview(sourcePath, draft, files, sortedSkipped, warnings, replaces, null, root);
        }
        catch (ImportRejectedException ex)
        {
            DeleteQuietly(root);
            return Failed(sourcePath, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _logger.LogWarning(ex, "匯入技能時讀寫失敗");
            DeleteQuietly(root);
            return Failed(sourcePath, ReadFailedProblem);
        }
        catch
        {
            DeleteQuietly(root);
            throw;
        }
    }

    /// <summary>找 SKILL.md：在根目錄或往下一層。0 個或超過一個都不能匯入。</summary>
    private static (string Prefix, string FallbackName) FindSkillFolder(IReadOnlyList<SourceItem> items, string sourceName)
    {
        var candidates = items
            .Where(i => i.Rel == SkillFile || (i.Rel.EndsWith("/" + SkillFile, StringComparison.Ordinal) && i.Rel.IndexOf('/') == i.Rel.LastIndexOf('/')))
            .ToList();
        if (candidates.Count == 0)
            throw new ImportRejectedException(NoSkillFileProblem);
        if (candidates.Count > 1)
            throw new ImportRejectedException(ManySkillsProblem);

        var prefix = candidates[0].Rel[..^SkillFile.Length];
        var fallback = prefix.Length == 0 ? sourceName : prefix.TrimEnd('/');
        return (prefix, fallback);
    }

    private static string? SkipReason(string rel)
    {
        if (IsScriptPath(rel))
            return ReasonScript;
        if (!SkillRules.IsTextFile(rel))
            return ReasonNotText;
        return null;
    }

    /// <summary>複製一個項目（或只讀取計數）。回傳實際讀到的位元組數。</summary>
    private static async Task<long> CopyItemAsync(SourceItem item, Stream destination, ImportBudget? budget, CancellationToken ct)
    {
        await using var source = item.Open();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            budget?.Add(read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return total;
    }

    private static List<string> BuildWarnings(IReadOnlyList<SkippedFile> skipped, IReadOnlyList<string> otherKeys, bool replaces, string name)
    {
        var warnings = new List<string>();
        if (skipped.Any(s => s.Reason is ReasonScript or ReasonNotText))
            warnings.Add(ScriptsWarning);
        if (otherKeys.Any(k => FieldsWarningKeys.Contains(k, StringComparer.OrdinalIgnoreCase)))
            warnings.Add(FieldsWarning);
        if (replaces)
            warnings.Add($"已經有名為「{name}」的技能，安裝後會取代它。");
        return warnings;
    }

    private static SkillImportPreview Failed(string path, string problem) =>
        new(path, new SkillDraft("", "", ""), [], [], [], false, problem, null);

    /// <summary>只接受 <see cref="_folder"/> 底下的 <c>.import-*</c> 暫存區，避免誤刪其他資料夾。</summary>
    private bool IsStagingPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        var full = Path.GetFullPath(path);
        return Path.GetFileName(full).StartsWith(ImportPrefix, StringComparison.Ordinal)
            && string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(_folder)), StringComparison.Ordinal);
    }

    private void DeleteQuietly(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "清理技能的暫存資料夾失敗");
        }
    }

    private void OnCatalogChanged(object? sender, AppSettings settings) =>
        Changed?.Invoke(this, EventArgs.Empty);
}
