using Lumi.Core.Models;

namespace Lumi.Core.Skills;

/// <summary>
/// <see cref="ISkillStore"/> 的記憶體版本，給測試與替身用（M29 Phase 0）。驗證走 <see cref="SkillRules"/>，行為和 <c>FileSkillStore</c> 一致：
/// 名稱不分大小寫、改名時停用狀態跟著搬、啟用中滿了就存成停用。
/// 有 <see cref="IModelCatalog"/> 時啟用狀態讀寫設定（catalog 不支援時退回自己記）；沒有時只記在這個物件裡。
/// 匯入只回傳 <see cref="NextImportPreview"/>（測試先設好）；沒設時回一個不能安裝的預覽。執行緒安全（一把鎖）。
/// </summary>
public sealed class InMemorySkillStore : ISkillStore
{
    private sealed record Entry(SkillDraft Draft, IReadOnlyDictionary<string, string> Files, string? Problem, bool HasIgnoredFiles);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _skills = new(SkillRules.NameComparer);
    private readonly HashSet<string> _disabled = new(SkillRules.NameComparer);
    private readonly IModelCatalog? _catalog;

    public InMemorySkillStore(IModelCatalog? catalog = null)
    {
        _catalog = catalog;
        if (catalog is not null)
            catalog.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public string Folder { get; init; } = Path.Combine(Path.GetTempPath(), "lumi-skills-in-memory");

    /// <summary>下一次 <see cref="PreviewImportAsync"/> 要回傳的預覽（測試用）。</summary>
    public SkillImportPreview? NextImportPreview { get; set; }

    /// <summary>呼叫過的 <see cref="RefreshAsync"/> 次數（測試用）。</summary>
    public int RefreshCount { get; private set; }

    /// <summary>測試準備資料：直接放一個技能（不檢查規則，所以可以放讀不進來的）。</summary>
    public void Add(SkillDraft draft, IReadOnlyDictionary<string, string>? files = null, string? problem = null, bool hasIgnoredFiles = false, bool enabled = true)
    {
        lock (_lock)
        {
            _skills[draft.Name] = new Entry(draft, files ?? new Dictionary<string, string>(), problem, hasIgnoredFiles);
        }
        // 有 catalog 時停用狀態寫進設定（之後用 SetSkillEnabledAsync 才改得回來）；catalog 不支援時才記在這裡
        if (!enabled)
            DisableAsync(draft.Name, CancellationToken.None).GetAwaiter().GetResult();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<SkillInfo> list = _skills.Values.Select(Info).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<SkillDocument?> GetAsync(string name, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_skills.TryGetValue(SkillRules.NormalizeName(name), out var entry))
                return Task.FromResult<SkillDocument?>(null);
            var files = entry.Files.Keys.Order(StringComparer.Ordinal).ToList();
            return Task.FromResult<SkillDocument?>(new SkillDocument(Info(entry), entry.Draft.Body, files));
        }
    }

    public Task<string?> ReadFileAsync(string name, string relativePath, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_skills.TryGetValue(SkillRules.NormalizeName(name), out var entry)
                || !SkillRules.TryNormalizeRelativePath(relativePath, out var path)
                || !entry.Files.TryGetValue(path, out var content))
                return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(content);
        }
    }

    public async Task<SkillSaveResult> SaveAsync(SkillDraft draft, CancellationToken ct)
    {
        if (SkillRules.Validate(draft) is { } problem)
            return new SkillSaveResult(SkillSaveOutcome.Invalid, problem);

        var normalized = new SkillDraft(
            SkillRules.NormalizeName(draft.Name), SkillRules.NormalizeDescription(draft.Description), SkillRules.NormalizeBody(draft.Body));
        var original = draft.OriginalName is null ? null : SkillRules.NormalizeName(draft.OriginalName);
        bool disable;
        lock (_lock)
        {
            Entry? existing = null;
            if (original is not null && !_skills.TryGetValue(original, out existing))
                return new SkillSaveResult(SkillSaveOutcome.NotFound, $"找不到技能「{original}」。");
            var renaming = original is not null && !SkillRules.NameComparer.Equals(original, normalized.Name);
            if ((original is null || renaming) && _skills.ContainsKey(normalized.Name))
                return new SkillSaveResult(SkillSaveOutcome.NameTaken, $"已經有名為「{normalized.Name}」的技能。");

            if (original is not null)
                _skills.Remove(original);
            _skills[normalized.Name] = new Entry(normalized, existing?.Files ?? new Dictionary<string, string>(), null, existing?.HasIgnoredFiles ?? false);
            disable = original is null && CountEnabled(except: normalized.Name) >= SkillRules.MaxEnabled;
            if (renaming && _disabled.Remove(original!))
                _disabled.Add(normalized.Name);
        }

        if (original is not null && !SkillRules.NameComparer.Equals(original, normalized.Name))
            await TryCatalogAsync(c => c.RenameSkillAsync(original, normalized.Name, ct)).ConfigureAwait(false);
        if (disable)
            await DisableAsync(normalized.Name, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return disable
            ? new SkillSaveResult(SkillSaveOutcome.SavedDisabled, $"已經有 {SkillRules.MaxEnabled} 個啟用中的技能，這個技能先存成停用。")
            : new SkillSaveResult(SkillSaveOutcome.Saved);
    }

    public Task DeleteAsync(string name, CancellationToken ct)
    {
        bool removed;
        lock (_lock)
            removed = _skills.Remove(SkillRules.NormalizeName(name));
        if (removed)
            Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<SkillImportPreview> PreviewImportAsync(string path, CancellationToken ct) =>
        Task.FromResult(NextImportPreview ?? new SkillImportPreview(path, new SkillDraft("", "", ""), [], [], [], false, "記憶體版本不支援匯入。"));

    public async Task<SkillSaveResult> InstallAsync(SkillImportPreview preview, CancellationToken ct)
    {
        if (!preview.CanInstall)
            return new SkillSaveResult(SkillSaveOutcome.Invalid, preview.Problem);
        await DeleteAsync(preview.Draft.Name, ct).ConfigureAwait(false);
        return await SaveAsync(preview.Draft with { OriginalName = null }, ct).ConfigureAwait(false);
    }

    public Task DiscardImportAsync(SkillImportPreview preview, CancellationToken ct) => Task.CompletedTask;

    public Task RefreshAsync(CancellationToken ct)
    {
        RefreshCount++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private SkillInfo Info(Entry entry) => new(
        entry.Draft.Name, SkillRules.NormalizeDescription(entry.Draft.Description), !IsDisabled(entry.Draft.Name), entry.HasIgnoredFiles, entry.Problem);

    private bool IsDisabled(string name)
    {
        if (_disabled.Contains(name))
            return true;
        return _catalog?.Current.Skills.IsDisabled(name) ?? false;
    }

    private int CountEnabled(string except) =>
        _skills.Values.Count(e => !SkillRules.NameComparer.Equals(e.Draft.Name, except) && e.Problem is null && !IsDisabled(e.Draft.Name));

    private async Task DisableAsync(string name, CancellationToken ct)
    {
        if (!await TryCatalogAsync(c => c.SetSkillEnabledAsync(name, false, ct)).ConfigureAwait(false))
        {
            lock (_lock)
                _disabled.Add(name);
        }
    }

    /// <summary>catalog 不支援（測試替身沒實作預設介面方法）時回 false。</summary>
    private async Task<bool> TryCatalogAsync(Func<IModelCatalog, Task> action)
    {
        if (_catalog is null)
            return false;
        try
        {
            await action(_catalog).ConfigureAwait(false);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
