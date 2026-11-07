using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Core.Skills;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定視窗「技能」（M29 5.7、包 29C）：啟用開關、用量、新增／編輯（列內或清單上方的表單，同時只會有一份）、刪除（先確認）、
/// 匯入（選資料夾或 zip → 預覽 → 安裝或取消）、打開技能資料夾。內容經由 <see cref="ISkillStore"/>，啟用狀態經由 <see cref="IModelCatalog"/>。
/// 技能變動（包括模型或別處改了、使用者自己放進資料夾）時 <see cref="ISkillStore.Changed"/> 會發出，這裡經由 <see cref="IUiDispatcher"/> 重新載入。
/// </summary>
public sealed partial class SkillsViewModel : ObservableObject, IDisposable
{
    private enum FormMode { None, New, Edit }

    private readonly ISkillStore _store;
    private readonly IModelCatalog _catalog;
    private readonly IConfirmService _confirm;
    private readonly IUiDispatcher _dispatcher;
    private readonly IFilePickerService? _files;
    private readonly IFolderOpener? _folders;
    private readonly List<SkillInfo> _infos = [];
    private readonly EventHandler _storeChanged;
    private FormMode _formMode;
    private int _generation;
    private bool _disposed;

    /// <param name="files">選資料夾或 zip；沒有時匯入按鈕什麼也不做（測試與沒有註冊的情況）。</param>
    /// <param name="folders">打開技能資料夾；沒有時該按鈕什麼也不做。</param>
    public SkillsViewModel(ISkillStore store, IModelCatalog catalog, IConfirmService confirm, IUiDispatcher dispatcher,
        IFilePickerService? files = null, IFolderOpener? folders = null)
    {
        _store = store;
        _catalog = catalog;
        _confirm = confirm;
        _dispatcher = dispatcher;
        _files = files;
        _folders = folders;
        Form = new SkillFormViewModel(SaveFormAsync, CloseForm);
        _storeChanged = (_, _) => _dispatcher.Post(() =>
        {
            if (!_disposed)
                _ = ReloadAsync();
        });
        store.Changed += _storeChanged;
    }

    /// <summary>清單：依名稱排序，含停用與讀不進來的技能。</summary>
    public ObservableCollection<SkillItemViewModel> Items { get; } = [];

    /// <summary>新增與編輯共用的表單（同一份物件；<see cref="NewForm"/> 或某一列的 <see cref="SkillItemViewModel.EditForm"/> 會掛上它）。</summary>
    public SkillFormViewModel Form { get; }

    /// <summary>新增表單是否開著（開著時顯示在清單上方）。</summary>
    public SkillFormViewModel? NewForm => _formMode == FormMode.New ? Form : null;

    /// <summary>匯入預覽（開著時顯示在清單上方）；沒有匯入時是 null。</summary>
    [ObservableProperty]
    private SkillImportViewModel? _importPreview;

    /// <summary>頁面訊息（達到上限、操作失敗）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public bool HasMessage => Message is not null;

    public bool IsEmpty => _infos.Count == 0;

    /// <summary>沒有技能時的說明（比照記憶頁，告訴使用者怎麼開始）。</summary>
    public string EmptyText => Strings.Skills_Empty;

    /// <summary>
    /// 用量文字，例如「已啟用 3／30 · 清單約佔 120 tokens」。tokens 用清單的字數除以 2 取整（和其他估算一致，偏保守）。
    /// </summary>
    public string UsageText
    {
        get
        {
            var enabled = _infos.Count(i => i.IsUsable);
            var tokens = SkillRules.ListCharacters(_infos) / 2;
            return string.Format(CultureInfo.CurrentCulture, Strings.Skills_UsageFormat,
                enabled.ToString("N0", CultureInfo.InvariantCulture),
                SkillRules.MaxEnabled.ToString("N0", CultureInfo.InvariantCulture),
                tokens.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>進入「技能」分類時呼叫：先請 store 重新掃描資料夾（使用者可能自己放了資料夾進去），再載入清單。</summary>
    public async Task LoadAsync()
    {
        try
        {
            await _store.RefreshAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(Strings.Privacy_OpenFailed, isError: true);
        }
        await ReloadAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _store.Changed -= _storeChanged;
    }

    /// <summary>標題列的主要動作：在清單上方打開新增表單。</summary>
    [RelayCommand]
    private void New()
    {
        Message = null;
        _formMode = FormMode.New;
        Form.Open(Strings.Skills_Add, originalName: null, name: "", description: "", body: "");
        ApplyFormState();
    }

    /// <summary>次要動作「匯入…」的「選資料夾」。</summary>
    [RelayCommand]
    private async Task ImportFolderAsync()
    {
        if (_files is null)
            return;
        var folder = await _files.PickFolderAsync();
        if (folder is not null)
            await StartImportAsync(folder);
    }

    /// <summary>次要動作「匯入…」的「選 zip 檔」：只取第一個，副檔名要是 .zip。</summary>
    [RelayCommand]
    private async Task ImportZipAsync()
    {
        if (_files is null)
            return;
        var picked = (await _files.PickFilesAsync()).FirstOrDefault();
        if (picked is null)
            return;
        if (!string.Equals(Path.GetExtension(picked), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            SetMessage(Strings.Skills_ImportNoSkill, isError: true);
            return;
        }
        await StartImportAsync(picked);
    }

    /// <summary>次要動作「打開技能資料夾」（資料夾不存在時先建立）。</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (_folders is null)
            return;
        try
        {
            var folder = _store.Folder;
            Directory.CreateDirectory(folder);
            if (!await _folders.OpenAsync(folder))
                SetMessage(Strings.Privacy_OpenFailed, isError: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(Strings.Privacy_OpenFailed, isError: true);
        }
    }

    /// <summary>
    /// 啟用開關（由列呼叫）。啟用中已滿 <see cref="SkillRules.MaxEnabled"/> 時擋下，說明寫在頁面訊息；存不進去時還原開關。
    /// </summary>
    private async Task SetEnabledAsync(SkillItemViewModel row, bool enabled)
    {
        if (!row.CanToggle)
        {
            row.Revert();
            return;
        }
        Message = null;
        if (enabled && _infos.Count(i => i.IsUsable) >= SkillRules.MaxEnabled)
        {
            SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Skills_EnableLimitFormat, SkillRules.MaxEnabled), isError: true);
            row.Revert();
            return;
        }
        try
        {
            await _catalog.SetSkillEnabledAsync(row.Name, enabled);
            row.SetEnabledState(enabled);
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetMessage(ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed, isError: true);
            row.Revert();   // 沒有存成功：開關回到原來的值
        }
    }

    /// <summary>列上的「編輯」：先取得完整內文，再打開表單（掛在那一列裡）。</summary>
    private async Task BeginEditAsync(SkillItemViewModel row)
    {
        Message = null;
        SkillDocument? document;
        try
        {
            document = await _store.GetAsync(row.Name, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(Strings.Privacy_OpenFailed, isError: true);
            return;
        }
        if (document is null)
        {
            await ReloadAsync();   // 已經不存在（例如別處刪掉了）：重新整理清單
            return;
        }
        _formMode = FormMode.Edit;
        Form.Open(Strings.Skills_Edit, document.Info.Name, document.Info.Name, document.Info.Description, document.Body);
        ApplyFormState();
    }

    /// <summary>列上的「刪除」：先確認，取消就什麼也不做。</summary>
    private async Task DeleteAsync(SkillItemViewModel row)
    {
        var confirmed = await _confirm.ConfirmDestructiveAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Skills_DeleteConfirmFormat, row.Name), Strings.Skills_Delete);
        if (!confirmed)
            return;
        try
        {
            await _store.DeleteAsync(row.Name, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(Strings.Settings_SaveFailed, isError: true);
            return;
        }
        if (_formMode == FormMode.Edit && SkillRules.NameComparer.Equals(Form.OriginalName, row.Name))
            CloseForm();
        Message = null;
        await ReloadAsync();
    }

    /// <summary>表單的「儲存」：新增或修改都走 <see cref="ISkillStore.SaveAsync"/>；只有成功才關表單。</summary>
    private async Task SaveFormAsync()
    {
        try
        {
            var result = await _store.SaveAsync(Form.ToDraft(), CancellationToken.None);
            if (!result.Succeeded)
            {
                Form.FormError = result.Message ?? Strings.Settings_SaveFailed;
                return;
            }
            CloseForm();
            if (result.Outcome == SkillSaveOutcome.SavedDisabled)
            {
                SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Skills_LimitReached, SkillRules.MaxEnabled), isError: false);
            }
            else
            {
                Message = null;
            }
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Form.FormError = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
        }
    }

    private async Task StartImportAsync(string path)
    {
        Message = null;
        SkillImportPreview preview;
        try
        {
            preview = await _store.PreviewImportAsync(path, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(Strings.Privacy_OpenFailed, isError: true);
            return;
        }

        // 同時只有一份預覽：換新的之前，先放掉舊的暫存資料夾
        if (ImportPreview is { } previous)
            await DiscardAsync(previous.Preview);
        ImportPreview = new SkillImportViewModel(preview, InstallImportAsync, CancelImportAsync);
    }

    private async Task InstallImportAsync()
    {
        var import = ImportPreview;
        if (import is null)
            return;
        try
        {
            var result = await _store.InstallAsync(import.Preview, CancellationToken.None);
            if (!result.Succeeded)
            {
                import.Error = result.Message ?? Strings.Settings_SaveFailed;
                return;
            }
            ImportPreview = null;
            if (result.Outcome == SkillSaveOutcome.SavedDisabled)
                SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Skills_LimitReached, SkillRules.MaxEnabled), isError: false);
            else
                Message = null;
            await ReloadAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            import.Error = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
        }
    }

    private async Task CancelImportAsync()
    {
        var import = ImportPreview;
        if (import is null)
            return;
        ImportPreview = null;
        await DiscardAsync(import.Preview);
    }

    private async Task DiscardAsync(SkillImportPreview preview)
    {
        try
        {
            await _store.DiscardImportAsync(preview, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 暫存資料夾刪不掉：下次啟動時 store 會清掉殘留的 .import-*，這裡不打擾使用者
        }
    }

    /// <summary>重新載入清單。較晚開始的載入會讓較早的結果作廢。</summary>
    private async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var infos = await _store.ListAsync(CancellationToken.None);
            if (_disposed || generation != _generation)
                return;
            Rebuild(infos);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_disposed)
                SetMessage(Strings.Privacy_OpenFailed, isError: true);
        }
    }

    private void Rebuild(IReadOnlyList<SkillInfo> infos)
    {
        _infos.Clear();
        _infos.AddRange(infos);

        Items.Clear();
        foreach (var info in _infos)
            Items.Add(new SkillItemViewModel(info, SetEnabledAsync, BeginEditAsync, DeleteAsync));

        // 編輯中的那一個已經不存在：關閉表單（清單已經沒有它可以掛）
        if (_formMode == FormMode.Edit && _infos.All(i => !SkillRules.NameComparer.Equals(i.Name, Form.OriginalName)))
            _formMode = FormMode.None;

        ApplyFormState();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(UsageText));
    }

    /// <summary>把表單掛到正確的位置：新增在清單上方，編輯掛在那一列裡；其他列沒有表單。</summary>
    private void ApplyFormState()
    {
        foreach (var row in Items)
        {
            row.EditForm = _formMode == FormMode.Edit && SkillRules.NameComparer.Equals(row.Name, Form.OriginalName) ? Form : null;
        }
        OnPropertyChanged(nameof(NewForm));
    }

    private void CloseForm()
    {
        _formMode = FormMode.None;
        Form.FormError = null;
        ApplyFormState();
    }

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}

/// <summary>清單的一列：名稱、描述、標籤、啟用開關，以及「編輯」「刪除」的入口。讀不進來的列沒有開關（<see cref="CanToggle"/>）。</summary>
public sealed partial class SkillItemViewModel : ObservableObject
{
    private readonly Func<SkillItemViewModel, bool, Task> _setEnabled;
    private bool _enabled;

    public SkillItemViewModel(SkillInfo info, Func<SkillItemViewModel, bool, Task> setEnabled, Func<SkillItemViewModel, Task> edit,
        Func<SkillItemViewModel, Task> delete)
    {
        Info = info;
        _setEnabled = setEnabled;
        _enabled = info.Enabled;
        EditCommand = new AsyncRelayCommand(() => edit(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
        Tags = BuildTags(info);
    }

    public SkillInfo Info { get; }

    public string Name => Info.Name;

    public string Description => Info.Description;

    public bool HasDescription => Description.Length > 0;

    /// <summary>讀不進來（<see cref="SkillInfo.Problem"/>）的列沒有開關。</summary>
    public bool CanToggle => Info.Problem is null;

    /// <summary>啟用開關（雙向）：改了就經由 <see cref="SkillsViewModel"/> 存進設定；失敗或超過上限時開關會回到原來的值。</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled)
                return;
            _ = _setEnabled(this, value);
        }
    }

    /// <summary>標籤：「已略過部分檔案」（Caption）、「讀不進來：原因」（Warning）。</summary>
    public IReadOnlyList<SkillTag> Tags { get; }

    public bool HasTags => Tags.Count > 0;

    /// <summary>這一列正在編輯時，共用的表單；否則是 null（表單不顯示）。</summary>
    [ObservableProperty]
    private SkillFormViewModel? _editForm;

    public IAsyncRelayCommand EditCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>開關沒有存成功：回到目前的值。</summary>
    internal void Revert() => OnPropertyChanged(nameof(IsEnabled));

    internal void SetEnabledState(bool value)
    {
        if (_enabled == value)
            return;
        _enabled = value;
        OnPropertyChanged(nameof(IsEnabled));
    }

    private static IReadOnlyList<SkillTag> BuildTags(SkillInfo info)
    {
        var tags = new List<SkillTag>();
        if (info.Problem is { } problem)
            tags.Add(new SkillTag(string.Format(CultureInfo.CurrentCulture, Strings.Skills_TagProblemFormat, problem), IsWarning: true));
        if (info.HasIgnoredFiles)
            tags.Add(new SkillTag(Strings.Skills_TagIgnoredFiles, IsWarning: false));
        return tags;
    }
}

/// <summary>列上的標籤（文字一定寫出來，不只靠顏色）。</summary>
public sealed record SkillTag(string Text, bool IsWarning);

/// <summary>
/// 新增與編輯共用的表單。欄位的格式檢查用 <see cref="SkillRules"/>（和 store 一致）；<see cref="SaveBlockedReason"/> 說明為什麼不能存。
/// 儲存與取消由 <see cref="SkillsViewModel"/> 處理。<see cref="OriginalName"/> 是 null＝新增，有值＝修改那個名稱的技能。
/// </summary>
public sealed partial class SkillFormViewModel : ObservableObject
{
    public SkillFormViewModel(Func<Task> save, Action cancel)
    {
        SaveCommand = new AsyncRelayCommand(save, () => CanSave);
        CancelCommand = new RelayCommand(cancel);
        PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    /// <summary>表單標題：「新增技能」或「編輯」。</summary>
    [ObservableProperty]
    private string _title = "";

    /// <summary>修改時是原本的名稱（改名時 store 會搬移）；新增時是 null。</summary>
    public string? OriginalName { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason), nameof(CanSave))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason), nameof(CanSave))]
    private string _description = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveBlockedReason), nameof(CanSave))]
    private string _body = "";

    /// <summary>儲存時 store 回的原因（名稱重複、寫入失敗）。欄位一改就清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string? _formError;

    public bool HasFormError => FormError is not null;

    partial void OnNameChanged(string value) => FormError = null;

    partial void OnDescriptionChanged(string value) => FormError = null;

    partial void OnBodyChanged(string value) => FormError = null;

    /// <summary>不能儲存的原因（欄位空白或超過上限）；能儲存時是 null。</summary>
    public string? SaveBlockedReason => SkillRules.Validate(ToDraft());

    public bool CanSave => SaveBlockedReason is null;

    public SkillDraft ToDraft() => new(Name, Description, Body, OriginalName);

    /// <summary>打開表單：新增（<paramref name="originalName"/> 為 null）或修改。</summary>
    public void Open(string title, string? originalName, string name, string description, string body)
    {
        Title = title;
        OriginalName = originalName;
        Name = name;
        Description = description;
        Body = body;
        FormError = null;
    }
}

/// <summary>匯入預覽的一列略過檔案：路徑與原因。</summary>
public sealed record SkippedFileRow(string Path, string Reason);

/// <summary>
/// 匯入預覽（頁面內的面板，比照表單）：名稱、描述、步驟全文、會複製的檔案、略過的檔案與原因、警告、不能安裝時的原因。
/// 安裝與取消由 <see cref="SkillsViewModel"/> 處理；取代同名技能時按鈕文字換成「取代並安裝」（警告文字由 store 提供）。
/// </summary>
public sealed partial class SkillImportViewModel : ObservableObject
{
    public SkillImportViewModel(SkillImportPreview preview, Func<Task> install, Func<Task> cancel)
    {
        Preview = preview;
        Files = preview.Files;
        Skipped = preview.Skipped.Select(s => new SkippedFileRow(s.Path, s.Reason)).ToList();
        Warnings = preview.Warnings;
        InstallCommand = new AsyncRelayCommand(install, () => preview.CanInstall);
        CancelCommand = new AsyncRelayCommand(cancel);
    }

    public SkillImportPreview Preview { get; }

    public string Name => Preview.Draft.Name;

    public string Description => Preview.Draft.Description;

    public string Body => Preview.Draft.Body;

    public IReadOnlyList<string> Files { get; }

    public bool HasFiles => Files.Count > 0;

    public IReadOnlyList<SkippedFileRow> Skipped { get; }

    public bool HasSkipped => Skipped.Count > 0;

    public IReadOnlyList<string> Warnings { get; }

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>不能安裝的原因（例如找不到 SKILL.md）；能安裝時是 null。</summary>
    public string? Problem => Preview.Problem;

    public bool HasProblem => Problem is not null;

    public bool CanInstall => Preview.CanInstall;

    /// <summary>會取代同名技能：「取代並安裝」；否則「安裝」。</summary>
    public string InstallText => Preview.ReplacesExisting ? Strings.Skills_ImportReplace : Strings.Skills_ImportInstall;

    public IAsyncRelayCommand InstallCommand { get; }

    public IAsyncRelayCommand CancelCommand { get; }

    /// <summary>安裝失敗的原因（就地顯示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;
}
