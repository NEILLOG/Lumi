using System.Globalization;
using System.Reflection;
using Lumi.App.Resources;
using Lumi.App.Tests.TestSupport;
using Lumi.App.ViewModels;
using Lumi.Core.Models;
using Lumi.Core.Skills;

namespace Lumi.App.Tests;

/// <summary>M29 包 29C：設定頁「技能」的 ViewModel——清單與開關（失敗還原、上限擋下）、新增／編輯／改名、存檔錯誤、刪除確認、匯入預覽、打開資料夾。</summary>
public sealed class SkillsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lumi-skills-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeModelCatalog _inner = new();
    private readonly IModelCatalog _catalog;
    private readonly SkillCatalogProxy _catalogProxy;
    private readonly RecordingSkillStore _store;
    private readonly FakeConfirmService _confirm = new();
    private readonly FakeFilePicker _files = new();
    private readonly FakeFolderOpener _folders = new();
    private readonly SkillsViewModel _vm;

    public SkillsViewModelTests()
    {
        _catalog = SkillCatalogProxy.Wrap(_inner, out _catalogProxy);
        _store = new RecordingSkillStore(new InMemorySkillStore(_catalog) { Folder = _folder });
        _vm = new SkillsViewModel(_store, _catalog, _confirm, new SyncUiDispatcher(), _files, _folders);
    }

    public void Dispose()
    {
        _vm.Dispose();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static SkillDraft Draft(string name, string description = "每週五整理本週工作", string body = "1. 整理\n2. 寫週報") =>
        new(name, description, body);

    private static SkillImportPreview Preview(string name, string body = "1. 步驟", string? problem = null, bool replaces = false,
        IReadOnlyList<string>? files = null, IReadOnlyList<SkippedFile>? skipped = null, IReadOnlyList<string>? warnings = null) =>
        new(@"C:\Downloads\weekly", new SkillDraft(name, "每週五整理本週工作", body), files ?? [], skipped ?? [], warnings ?? [], replaces, problem);

    private SkillItemViewModel Row(string name) => _vm.Items.Single(i => i.Name == name);

    private void AddEnabled(int count)
    {
        for (var i = 1; i <= count; i++)
            _store.Inner.Add(Draft($"技能{i:00}"));
    }

    /// <summary>用設定把技能停用（和使用者在畫面上關掉開關一樣，走 catalog）。</summary>
    private void Disable(string name) =>
        _inner.Current = _inner.Current with { Skills = _inner.Current.Skills.WithEnabled(name, false) };

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    [Fact]
    public async Task Load_ShowsEveryStoredSkill_SortedByName_AndRescansTheFolder()
    {
        _store.Inner.Add(Draft("週報"));
        _store.Inner.Add(Draft("會議紀錄"));

        await _vm.LoadAsync();

        Assert.Equal(["會議紀錄", "週報"], _vm.Items.Select(i => i.Name));
        Assert.Equal(1, _store.Inner.RefreshCount);
        Assert.False(_vm.IsEmpty);
    }

    [Fact]
    public async Task Empty_ShowsTheEmptyExplanation_AndZeroUsage()
    {
        await _vm.LoadAsync();

        Assert.True(_vm.IsEmpty);
        Assert.Equal(Strings.Skills_Empty, _vm.EmptyText);
        Assert.Equal(Format(Strings.Skills_UsageFormat, "0", "30", "0"), _vm.UsageText);
    }

    [Fact]
    public async Task Usage_CountsOnlyEnabledSkills_AndEstimatesTokensFromTheListCharacters()
    {
        // 名稱 2 字＋描述 5 字＋4 ＝ 11 字 → 5 tokens（SkillRules.ListCharacters ÷ 2 取整）
        _store.Inner.Add(Draft("週報", description: "每週五整理"));
        _store.Inner.Add(Draft("停用的", description: "每週五整理"));
        Disable("停用的");
        await _vm.LoadAsync();

        Assert.Equal(Format(Strings.Skills_UsageFormat, "1", "30", "5"), _vm.UsageText);
    }

    [Fact]
    public async Task Toggle_Disables_ThroughTheCatalog_AndUpdatesTheUsage()
    {
        _store.Inner.Add(Draft("週報"));
        await _vm.LoadAsync();

        Row("週報").IsEnabled = false;
        await Task.Yield();

        Assert.True(_inner.Current.Skills.IsDisabled("週報"));
        Assert.False(Row("週報").IsEnabled);
        Assert.Equal(Format(Strings.Skills_UsageFormat, "0", "30", "0"), _vm.UsageText);
        Assert.False(_vm.HasMessage);
    }

    [Fact]
    public async Task Toggle_Enables_AgainWhenThereIsRoom()
    {
        _store.Inner.Add(Draft("週報"));
        Disable("週報");
        await _vm.LoadAsync();

        Row("週報").IsEnabled = true;

        Assert.False(_inner.Current.Skills.IsDisabled("週報"));
        Assert.True(Row("週報").IsEnabled);
    }

    [Fact]
    public async Task Toggle_WhenTheSaveFails_RevertsTheSwitch_AndShowsTheError()
    {
        _store.Inner.Add(Draft("週報"));
        await _vm.LoadAsync();
        _catalogProxy.Failure = new IOException("disk full");

        Row("週報").IsEnabled = false;
        await Task.Yield();

        Assert.True(Row("週報").IsEnabled);
        Assert.Equal(Strings.Settings_SaveFailed, _vm.Message);
        Assert.True(_vm.MessageIsError);
    }

    [Fact]
    public async Task Toggle_AtTheLimit_IsBlocked_AndExplained()
    {
        AddEnabled(SkillRules.MaxEnabled);
        _store.Inner.Add(Draft("停用的"));
        Disable("停用的");
        await _vm.LoadAsync();

        Row("停用的").IsEnabled = true;

        Assert.False(Row("停用的").IsEnabled);
        Assert.True(_inner.Current.Skills.IsDisabled("停用的"));
        Assert.Equal(Format(Strings.Skills_EnableLimitFormat, SkillRules.MaxEnabled), _vm.Message);
        Assert.True(_vm.MessageIsError);
    }

    [Fact]
    public async Task UnreadableSkill_HasNoSwitch_AndTheProblemTag()
    {
        _store.Inner.Add(Draft("壞掉的"), problem: "找不到說明");
        await _vm.LoadAsync();

        var row = Row("壞掉的");
        Assert.False(row.CanToggle);
        Assert.Equal(Format(Strings.Skills_TagProblemFormat, "找不到說明"), row.Tags.Single().Text);
        Assert.True(row.Tags.Single().IsWarning);

        row.IsEnabled = false;   // 沒有開關就不能改
        await Task.Yield();
        Assert.False(_inner.Current.Skills.IsDisabled("壞掉的"));
        Assert.True(row.IsEnabled);
    }

    [Fact]
    public async Task Row_ShowsTheIgnoredFilesTag_WhenTheFolderHadScripts()
    {
        _store.Inner.Add(Draft("有腳本"), hasIgnoredFiles: true);
        await _vm.LoadAsync();

        Assert.Equal(Strings.Skills_TagIgnoredFiles, Row("有腳本").Tags.Single().Text);
        Assert.False(Row("有腳本").Tags.Single().IsWarning);
    }

    [Fact]
    public async Task NewSkill_SavesAndClosesTheForm_AndListsIt()
    {
        await _vm.LoadAsync();
        _vm.NewCommand.Execute(null);
        Assert.NotNull(_vm.NewForm);
        Assert.Same(_vm.Form, _vm.NewForm);

        _vm.Form.Name = "週報";
        _vm.Form.Description = "每週五整理本週工作";
        _vm.Form.Body = "1. 整理\n2. 寫週報";
        Assert.True(_vm.Form.CanSave);
        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Null(_vm.NewForm);
        Assert.Equal("週報", Row("週報").Name);
        Assert.False(_vm.HasMessage);
    }

    [Fact]
    public void Form_CannotSave_WithBlankFields_AndSaysWhy()
    {
        _vm.NewCommand.Execute(null);

        Assert.False(_vm.Form.CanSave);
        Assert.False(_vm.Form.SaveCommand.CanExecute(null));
        Assert.Equal(SkillRules.ValidateName(""), _vm.Form.SaveBlockedReason);
    }

    [Fact]
    public async Task Edit_LoadsTheFullBody_AndRenames()
    {
        _store.Inner.Add(Draft("週報", body: "1. 整理\n2. 寫週報"));
        await _vm.LoadAsync();

        Row("週報").EditCommand.Execute(null);
        await Task.Yield();

        Assert.Same(_vm.Form, Row("週報").EditForm);
        Assert.Null(_vm.NewForm);
        Assert.Equal("週報", _vm.Form.OriginalName);
        Assert.Equal("1. 整理\n2. 寫週報", _vm.Form.Body);

        _vm.Form.Name = "週報（新版）";
        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Null(Row("週報（新版）").EditForm);
        Assert.Equal(["週報（新版）"], _vm.Items.Select(i => i.Name));
        Assert.Null(_vm.Items.Single().EditForm);
        Assert.Equal("1. 整理\n2. 寫週報", (await _store.GetAsync("週報（新版）", default))!.Body);
    }

    [Fact]
    public async Task Save_WhenTheNameIsTaken_ShowsTheReasonInTheForm_AndKeepsTheForm()
    {
        _store.Inner.Add(Draft("週報"));
        _store.Inner.Add(Draft("會議"));
        await _vm.LoadAsync();
        _vm.NewCommand.Execute(null);
        _vm.Form.Name = "週報";
        _vm.Form.Description = "每週五整理本週工作";
        _vm.Form.Body = "1. 整理";

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(_vm.NewForm);
        Assert.True(_vm.Form.HasFormError);
        Assert.Contains("週報", _vm.Form.FormError);
        Assert.False(_vm.HasMessage);
    }

    [Fact]
    public async Task Save_WhenTheDiskFails_ShowsTheErrorInTheForm()
    {
        await _vm.LoadAsync();
        _vm.NewCommand.Execute(null);
        _vm.Form.Name = "週報";
        _vm.Form.Description = "每週五整理本週工作";
        _vm.Form.Body = "1. 整理";
        _store.SaveFailure = new IOException("disk");

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(_vm.NewForm);
        Assert.Equal(Strings.Settings_SaveFailed, _vm.Form.FormError);
    }

    [Fact]
    public async Task Save_WhenTheLimitIsReached_SavesDisabled_AndShowsTheLimitMessage()
    {
        AddEnabled(SkillRules.MaxEnabled);
        await _vm.LoadAsync();
        _vm.NewCommand.Execute(null);
        _vm.Form.Name = "新的";
        _vm.Form.Description = "每週五整理本週工作";
        _vm.Form.Body = "1. 整理";

        await _vm.Form.SaveCommand.ExecuteAsync(null);

        Assert.Null(_vm.NewForm);
        Assert.False(Row("新的").Info.Enabled);
        Assert.Equal(Format(Strings.Skills_LimitReached, SkillRules.MaxEnabled), _vm.Message);
        Assert.False(_vm.MessageIsError);
    }

    [Fact]
    public async Task Delete_WhenTheUserCancels_KeepsTheSkill()
    {
        _store.Inner.Add(Draft("週報"));
        await _vm.LoadAsync();
        _confirm.Answer = false;

        await Row("週報").DeleteCommand.ExecuteAsync(null);

        Assert.Single(_confirm.Asked);
        Assert.Contains("週報", _confirm.Asked[0].Message);
        Assert.Equal(Strings.Skills_Delete, _confirm.Asked[0].ConfirmText);
        Assert.Equal(["週報"], _vm.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Delete_WhenConfirmed_RemovesTheSkill()
    {
        _store.Inner.Add(Draft("週報"));
        _store.Inner.Add(Draft("會議"));
        await _vm.LoadAsync();

        await Row("週報").DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["會議"], _vm.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Delete_OfTheSkillBeingEdited_ClosesTheForm()
    {
        _store.Inner.Add(Draft("週報"));
        await _vm.LoadAsync();
        Row("週報").EditCommand.Execute(null);
        await Task.Yield();

        await Row("週報").DeleteCommand.ExecuteAsync(null);

        Assert.Empty(_vm.Items);
        Assert.Null(_vm.NewForm);
    }

    [Fact]
    public async Task Changes_FromTheStore_ReloadTheList()
    {
        await _vm.LoadAsync();
        Assert.Empty(_vm.Items);

        _store.Inner.Add(Draft("週報"));   // 例如使用者自己放進資料夾並重新整理，或模型剛存了一個

        Assert.Equal(["週報"], _vm.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task ImportFolder_ShowsThePreview_AndInstalls()
    {
        await _vm.LoadAsync();
        _files.Folder = @"C:\Downloads\weekly";
        _store.Inner.NextImportPreview = Preview("週報", files: ["templates/week.md"],
            skipped: [new SkippedFile("scripts/run.py", "不會複製腳本")], warnings: ["附帶腳本，Lumi 不會執行。"]);

        await _vm.ImportFolderCommand.ExecuteAsync(null);

        var import = _vm.ImportPreview!;
        Assert.NotNull(import);
        Assert.Equal("週報", import.Name);
        Assert.True(import.CanInstall);
        Assert.Equal(["templates/week.md"], import.Files);
        Assert.True(import.HasFiles);
        Assert.Equal("scripts/run.py", import.Skipped.Single().Path);
        Assert.Equal("不會複製腳本", import.Skipped.Single().Reason);
        Assert.Equal("附帶腳本，Lumi 不會執行。", import.Warnings.Single());
        Assert.Equal(Strings.Skills_ImportInstall, import.InstallText);

        await import.InstallCommand.ExecuteAsync(null);

        Assert.Null(_vm.ImportPreview);
        Assert.Equal(["週報"], _vm.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task ImportFolder_ReplacingASkill_UsesTheReplaceLabel()
    {
        _store.Inner.Add(Draft("週報"));
        await _vm.LoadAsync();
        _files.Folder = @"C:\Downloads\weekly";
        _store.Inner.NextImportPreview = Preview("週報", replaces: true);

        await _vm.ImportFolderCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Skills_ImportReplace, _vm.ImportPreview!.InstallText);
    }

    [Fact]
    public async Task ImportFolder_WithAProblem_CannotInstall_AndOnlyCancelRemains()
    {
        await _vm.LoadAsync();
        _files.Folder = @"C:\Downloads\weekly";
        _store.Inner.NextImportPreview = Preview("週報", problem: Strings.Skills_ImportNoSkill);

        await _vm.ImportFolderCommand.ExecuteAsync(null);

        var import = _vm.ImportPreview!;
        Assert.True(import.HasProblem);
        Assert.Equal(Strings.Skills_ImportNoSkill, import.Problem);
        Assert.False(import.CanInstall);
        Assert.False(import.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task ImportPreview_Cancel_DiscardsTheStagingFolder_AndClosesThePreview()
    {
        await _vm.LoadAsync();
        _files.Folder = @"C:\Downloads\weekly";
        _store.Inner.NextImportPreview = Preview("週報");
        await _vm.ImportFolderCommand.ExecuteAsync(null);
        var preview = _vm.ImportPreview!.Preview;

        await _vm.ImportPreview!.CancelCommand.ExecuteAsync(null);

        Assert.Null(_vm.ImportPreview);
        Assert.Same(preview, Assert.Single(_store.Discarded));
        Assert.Empty(_vm.Items);
    }

    [Fact]
    public async Task ImportAnother_WhileOnePreviewIsOpen_DiscardsTheOldOne()
    {
        await _vm.LoadAsync();
        _files.Folder = @"C:\Downloads\weekly";
        _store.Inner.NextImportPreview = Preview("第一個");
        await _vm.ImportFolderCommand.ExecuteAsync(null);
        var first = _vm.ImportPreview!.Preview;
        _store.Inner.NextImportPreview = Preview("第二個");

        await _vm.ImportFolderCommand.ExecuteAsync(null);

        Assert.Same(first, Assert.Single(_store.Discarded));
        Assert.Equal("第二個", _vm.ImportPreview!.Name);
    }

    [Fact]
    public async Task ImportZip_WhenThePickedFileIsNotAZip_SaysSo()
    {
        await _vm.LoadAsync();
        _files.Files.Add(@"C:\Downloads\notes.txt");

        await _vm.ImportZipCommand.ExecuteAsync(null);

        Assert.Null(_vm.ImportPreview);
        Assert.Equal(Strings.Skills_ImportNoSkill, _vm.Message);
        Assert.True(_vm.MessageIsError);
    }

    [Fact]
    public async Task ImportZip_TakesTheFirstZip_AndOpensItsPreview()
    {
        await _vm.LoadAsync();
        _files.Files.Add(@"C:\Downloads\weekly.ZIP");
        _files.Files.Add(@"C:\Downloads\other.zip");
        _store.Inner.NextImportPreview = Preview("週報");

        await _vm.ImportZipCommand.ExecuteAsync(null);

        Assert.Equal("週報", _vm.ImportPreview!.Name);
    }

    [Fact]
    public async Task Install_WhenTheStoreRefuses_ShowsTheReasonInThePreview()
    {
        await _vm.LoadAsync();
        _files.Folder = @"C:\Downloads\weekly";
        _store.Inner.NextImportPreview = Preview("週報", body: "");   // 內文空白：store 的檢查不過

        await _vm.ImportFolderCommand.ExecuteAsync(null);
        await _vm.ImportPreview!.InstallCommand.ExecuteAsync(null);

        Assert.NotNull(_vm.ImportPreview);
        Assert.True(_vm.ImportPreview.HasError);
        Assert.Equal(SkillRules.ValidateBody(""), _vm.ImportPreview.Error);
    }

    [Fact]
    public async Task OpenFolder_CreatesTheFolder_AndOpensIt()
    {
        var folder = Path.Combine(_folder, "skills");
        var store = new RecordingSkillStore(new InMemorySkillStore(_catalog) { Folder = folder });
        using var vm = new SkillsViewModel(store, _catalog, _confirm, new SyncUiDispatcher(), _files, _folders);

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.True(Directory.Exists(folder));
        Assert.Equal([folder], _folders.Opened);
        Assert.False(vm.HasMessage);
    }

    [Fact]
    public async Task OpenFolder_WhenTheSystemCannotOpenIt_SaysSo()
    {
        using var vm = new SkillsViewModel(_store, _catalog, _confirm, new SyncUiDispatcher(), _files, new FakeFolderOpener(succeeds: false));

        await vm.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Privacy_OpenFailed, vm.Message);
        Assert.True(vm.MessageIsError);
    }

    [Fact]
    public async Task EditingAFormOpen_AndThenNewing_OnlyOneFormIsShown()
    {
        _store.Inner.Add(Draft("週報"));
        await _vm.LoadAsync();
        Row("週報").EditCommand.Execute(null);
        await Task.Yield();

        _vm.NewCommand.Execute(null);

        Assert.NotNull(_vm.NewForm);
        Assert.Null(Row("週報").EditForm);
        Assert.Null(_vm.Form.OriginalName);
    }

    /// <summary>
    /// 把 <see cref="IModelCatalog"/> 的技能開關接到 <see cref="FakeModelCatalog"/> 的設定快照上（假目錄不支援這個方法）。
    /// 可以指定失敗，用來測試開關存不進去的情況。
    /// </summary>
    public class SkillCatalogProxy : DispatchProxy
    {
        private FakeModelCatalog _inner = null!;

        /// <summary>設為非 null 時，<see cref="IModelCatalog.SetSkillEnabledAsync"/> 失敗。</summary>
        public Exception? Failure { get; set; }

        internal static IModelCatalog Wrap(FakeModelCatalog inner, out SkillCatalogProxy typed)
        {
            var proxy = Create<IModelCatalog, SkillCatalogProxy>();
            typed = (SkillCatalogProxy)proxy;
            typed._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IModelCatalog.SetSkillEnabledAsync))
            {
                if (Failure is not null)
                    return Task.FromException(Failure);
                var name = (string)args![0]!;
                var enabled = (bool)args[1]!;
                _inner.Current = _inner.Current with { Skills = _inner.Current.Skills.WithEnabled(name, enabled) };
                return Task.CompletedTask;
            }
            if (targetMethod.Name == nameof(IModelCatalog.RenameSkillAsync))
            {
                var (oldName, newName) = ((string)args![0]!, (string)args[1]!);
                _inner.Current = _inner.Current with { Skills = _inner.Current.Skills.WithRenamed(oldName, newName) };
                return Task.CompletedTask;
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}

/// <summary>包住 <see cref="InMemorySkillStore"/>，記下丟棄過的匯入預覽，並可以讓存檔失敗（測試用）。</summary>
internal sealed class RecordingSkillStore(InMemorySkillStore inner) : ISkillStore
{
    public InMemorySkillStore Inner { get; } = inner;

    public List<SkillImportPreview> Discarded { get; } = [];

    /// <summary>設為非 null 時，<see cref="SaveAsync"/> 丟出這個例外（模擬寫檔失敗）。</summary>
    public Exception? SaveFailure { get; set; }

    public event EventHandler? Changed
    {
        add => Inner.Changed += value;
        remove => Inner.Changed -= value;
    }

    public string Folder => Inner.Folder;

    public Task<IReadOnlyList<SkillInfo>> ListAsync(CancellationToken ct) => Inner.ListAsync(ct);

    public Task<SkillDocument?> GetAsync(string name, CancellationToken ct) => Inner.GetAsync(name, ct);

    public Task<string?> ReadFileAsync(string name, string relativePath, CancellationToken ct) => Inner.ReadFileAsync(name, relativePath, ct);

    public Task<SkillSaveResult> SaveAsync(SkillDraft draft, CancellationToken ct) =>
        SaveFailure is { } failure ? Task.FromException<SkillSaveResult>(failure) : Inner.SaveAsync(draft, ct);

    public Task DeleteAsync(string name, CancellationToken ct) => Inner.DeleteAsync(name, ct);

    public Task<SkillImportPreview> PreviewImportAsync(string path, CancellationToken ct) => Inner.PreviewImportAsync(path, ct);

    public Task<SkillSaveResult> InstallAsync(SkillImportPreview preview, CancellationToken ct) => Inner.InstallAsync(preview, ct);

    public Task DiscardImportAsync(SkillImportPreview preview, CancellationToken ct)
    {
        Discarded.Add(preview);
        return Inner.DiscardImportAsync(preview, ct);
    }

    public Task RefreshAsync(CancellationToken ct) => Inner.RefreshAsync(ct);
}
