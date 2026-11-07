namespace Lumi.Core.Skills;

/// <summary>
/// 清單用的一個技能（M29），不含內文。<paramref name="Problem"/> 不是 null 時表示這個技能讀不進來：設定頁顯示原因，不送給模型。
/// <paramref name="HasIgnoredFiles"/>：資料夾裡有 Lumi 不認得、不會提供給模型的檔案（例如腳本）。
/// </summary>
public sealed record SkillInfo(string Name, string Description, bool Enabled, bool HasIgnoredFiles, string? Problem)
{
    /// <summary>會列進 system 第 8 層、能被 <c>read_skill</c> 讀取。</summary>
    public bool IsUsable => Enabled && Problem is null;
}

/// <summary>完整內容：內文（不含 frontmatter）與附帶的文字檔（相對路徑，用 <c>/</c> 分隔，依 Ordinal 排序，不含 SKILL.md）。</summary>
public sealed record SkillDocument(SkillInfo Info, string Body, IReadOnlyList<string> Files);

/// <summary>新增、修改共用。<paramref name="OriginalName"/> 不是 null＝修改既有的技能（可以改名）。</summary>
public sealed record SkillDraft(string Name, string Description, string Body, string? OriginalName = null);

/// <summary><see cref="SavedDisabled"/>：存好了，但啟用中已經滿了，所以存成停用（D4）。</summary>
public enum SkillSaveOutcome { Saved, SavedDisabled, NameTaken, Invalid, NotFound }

/// <param name="Message">不是 <see cref="SkillSaveOutcome.Saved"/> 時可以直接顯示的原因。</param>
public sealed record SkillSaveResult(SkillSaveOutcome Outcome, string? Message = null)
{
    public bool Succeeded => Outcome is SkillSaveOutcome.Saved or SkillSaveOutcome.SavedDisabled;
}

/// <summary>匯入時沒有複製的檔案與原因。</summary>
public sealed record SkippedFile(string Path, string Reason);

/// <summary>
/// 匯入預覽（29A）。<paramref name="Problem"/> 不是 null 時不能安裝（例如找不到 SKILL.md、超過大小），其他欄位可能是空的。
/// <paramref name="Files"/>：會複製的附帶檔案（相對路徑，不含 SKILL.md）。<paramref name="Warnings"/>：可以直接顯示的警告文字。
/// <paramref name="StagingPath"/>：解壓與檢查後的暫存資料夾，<see cref="ISkillStore.InstallAsync"/> 從這裡搬；store 以外不要碰。
/// </summary>
public sealed record SkillImportPreview(
    string SourcePath,
    SkillDraft Draft,
    IReadOnlyList<string> Files,
    IReadOnlyList<SkippedFile> Skipped,
    IReadOnlyList<string> Warnings,
    bool ReplacesExisting,
    string? Problem,
    string? StagingPath = null)
{
    public bool CanInstall => Problem is null;
}

/// <summary>
/// 技能的儲存（M29）。實作是 <c>Lumi.Infrastructure.Skills.FileSkillStore</c>：一個技能一個資料夾，<c>{Folder}/{名稱}/SKILL.md</c>。
/// 名稱一律不分大小寫比對。啟用狀態存在設定檔（<see cref="SkillSettings.Disabled"/>），這裡只負責讀出來合併到 <see cref="SkillInfo.Enabled"/>。
/// </summary>
public interface ISkillStore
{
    /// <summary>技能新增、修改、刪除、重新掃描，或啟用狀態改變時。可能從任何執行緒發出。</summary>
    event EventHandler? Changed;

    /// <summary>技能資料夾的完整路徑（設定頁「打開技能資料夾」、資料與隱私頁）。</summary>
    string Folder { get; }

    /// <summary>全部技能（含停用與讀不進來的），依名稱 Ordinal 排序。有快取；<see cref="RefreshAsync"/> 重新掃描資料夾。</summary>
    Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken ct);

    /// <summary>找不到回 null。讀不進來的技能也回傳（<see cref="SkillInfo.Problem"/> 有值、內文可能是空的）。</summary>
    Task<SkillDocument?> GetAsync(string name, CancellationToken ct);

    /// <summary>附帶檔案的內容。路徑必須是 <see cref="SkillDocument.Files"/> 裡的一項；回 null＝不存在或不允許。</summary>
    Task<string?> ReadFileAsync(string name, string relativePath, CancellationToken ct);

    /// <summary>新增或修改（<see cref="SkillDraft.OriginalName"/>）。修改時只換 SKILL.md 的名稱、描述與內文，附帶檔案與其他 frontmatter 欄位保留。</summary>
    Task<SkillSaveResult> SaveAsync(SkillDraft draft, CancellationToken ct);

    /// <summary>刪除整個資料夾。找不到時什麼也不做。</summary>
    Task DeleteAsync(string name, CancellationToken ct);

    /// <summary>檢查資料夾或 <c>.zip</c>，產生預覽。不會動到正式的技能。</summary>
    Task<SkillImportPreview> PreviewImportAsync(string path, CancellationToken ct);

    /// <summary>安裝預覽過的技能（取代同名）。</summary>
    Task<SkillSaveResult> InstallAsync(SkillImportPreview preview, CancellationToken ct);

    /// <summary>放棄預覽：刪掉暫存資料夾。</summary>
    Task DiscardImportAsync(SkillImportPreview preview, CancellationToken ct);

    /// <summary>重新掃描資料夾（使用者可能自己放了資料夾進去）。</summary>
    Task RefreshAsync(CancellationToken ct);
}
