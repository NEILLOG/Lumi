using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Memory;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>分類選項（表單的下拉選單）。文字用 <see cref="MemoryRules.CategoryName"/>。</summary>
public sealed record MemoryCategoryOption(MemoryCategory Category)
{
    public string Text => MemoryRules.CategoryName(Category);

    public static IReadOnlyList<MemoryCategoryOption> All { get; } =
        Enum.GetValues<MemoryCategory>().Select(c => new MemoryCategoryOption(c)).ToList();
}

/// <summary>
/// 新增與編輯共用的表單（列內展開，同時只會有一個）。欄位與格式檢查在這裡；字數以正規化後的內容計（和 store 的規則一致）；
/// 到期日留空＝永久。儲存與取消由 <see cref="MemoryViewModel"/> 處理。
/// </summary>
public partial class MemoryFormViewModel : ObservableObject
{
    private readonly MemoryOptions _options;

    public MemoryFormViewModel(MemoryOptions options, Func<Task> save, Action cancel)
    {
        _options = options;
        SaveCommand = new AsyncRelayCommand(save, () => CanSave);
        CancelCommand = new RelayCommand(cancel);
        PropertyChanged += (_, _) => SaveCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IReadOnlyList<MemoryCategoryOption> Categories => MemoryCategoryOption.All;

    /// <summary>表單標題：「新增記憶」或「編輯 m3」。</summary>
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentLength), nameof(ContentCounter), nameof(ContentError), nameof(HasContentError),
        nameof(CanSave), nameof(SaveBlockedReason))]
    private string _content = "";

    [ObservableProperty]
    private MemoryCategoryOption _selectedCategory = MemoryCategoryOption.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpiresDate), nameof(ExpiresError), nameof(HasExpiresError), nameof(CanSave), nameof(SaveBlockedReason))]
    private string _expires = "";

    /// <summary>儲存時 store 回的原因（重複、上限）。欄位一改就清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFormError))]
    private string? _formError;

    partial void OnContentChanged(string value) => FormError = null;

    partial void OnSelectedCategoryChanged(MemoryCategoryOption value) => FormError = null;

    partial void OnExpiresChanged(string value) => FormError = null;

    public bool HasFormError => FormError is not null;

    /// <summary>內容去掉換行與連續空白後的字數。</summary>
    public int ContentLength => MemoryRules.Length(MemoryRules.NormalizeContent(Content));

    /// <summary>字數的計數文字，例如「42／200」。</summary>
    public string ContentCounter => string.Format(CultureInfo.CurrentCulture, Strings.Memory_Counter, ContentLength, _options.MaxContentLength);

    /// <summary>內容超過單則上限的說明；空白時不顯示（改由 <see cref="SaveBlockedReason"/> 說明）。</summary>
    public string? ContentError => ContentLength == 0
        ? null
        : MemoryRules.ValidateContent(MemoryRules.NormalizeContent(Content), _options, checkSecret: false);

    public bool HasContentError => ContentError is not null;

    /// <summary>到期日；留空或格式錯誤時是 null（留空＝永久，格式錯由 <see cref="ExpiresError"/> 說明）。</summary>
    public DateOnly? ExpiresDate => MemoryRules.TryParseDate(Expires, out var date) ? date : null;

    public string? ExpiresError => string.IsNullOrWhiteSpace(Expires) || MemoryRules.TryParseDate(Expires, out _)
        ? null
        : Strings.Memory_ExpiresError;

    public bool HasExpiresError => ExpiresError is not null;

    /// <summary>內容有字、沒有超過上限、到期日格式正確（或留空）。</summary>
    public bool CanSave => ContentLength > 0 && ContentError is null && ExpiresError is null;

    /// <summary>儲存按鈕停用時寫在旁邊的原因（Caption）；能儲存時是 null。</summary>
    public string? SaveBlockedReason => ContentLength == 0
        ? Strings.Memory_ContentEmpty
        : ContentError is not null
            ? Strings.Memory_ContentBlocked
            : ExpiresError is not null
                ? Strings.Memory_ExpiresBlocked
                : null;

    /// <summary>打開表單：新增（空白、分類第一項、永久）或編輯（帶入原本的值）。</summary>
    public void Open(string title, string content, MemoryCategory category, DateOnly? expires)
    {
        Title = title;
        Content = content;
        SelectedCategory = MemoryCategoryOption.All.First(o => o.Category == category);
        Expires = expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        FormError = null;
    }
}

/// <summary>清單中的一則記憶：顯示用的文字與狀態，加上「編輯」「刪除」的入口。</summary>
public sealed partial class MemoryItemViewModel : ObservableObject
{
    public MemoryItemViewModel(MemoryItem item, DateOnly today, Action<MemoryItemViewModel> edit, Func<MemoryItemViewModel, Task> delete)
    {
        Item = item;
        IsExpired = !MemoryRules.IsActive(item, today);
        EditCommand = new RelayCommand(() => edit(this));
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public MemoryItem Item { get; }

    public int Id => Item.Id;

    /// <summary>給人看的代號，例如「m3」。</summary>
    public string IdText => MemoryRules.FormatId(Item.Id);

    public string Content => Item.Content;

    public MemoryCategory Category => Item.Category;

    public DateOnly? ExpiresAt => Item.ExpiresAt;

    /// <summary>來源徽章的文字：你要求的／Lumi 建議的／手動新增。</summary>
    public string SourceText => Item.Source switch
    {
        MemorySource.Explicit => Strings.Memory_SourceExplicit,
        MemorySource.Suggested => Strings.Memory_SourceSuggested,
        _ => Strings.Memory_SourceManual,
    };

    /// <summary>到期日的說明（「有效至 2026-12-31」）；永久是 null。</summary>
    public string? ValidUntilText => Item.ExpiresAt is { } date
        ? string.Format(CultureInfo.CurrentCulture, Strings.Memory_ValidUntil, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        : null;

    /// <summary>已過期（到期日早於今天）：仍在設定頁，但不送給模型。</summary>
    public bool IsExpired { get; }

    /// <summary>這一列正在編輯時，共用的表單；否則是 null（表單不顯示）。</summary>
    [ObservableProperty]
    private MemoryFormViewModel? _editForm;

    public IRelayCommand EditCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }
}

/// <summary>清單的一個分類群組（空的分類不建立）。</summary>
public sealed record MemoryGroupViewModel(MemoryCategory Category, string Title, IReadOnlyList<MemoryItemViewModel> Items);

/// <summary>
/// 設定視窗「記憶」：總開關、用量、依分類分組的清單、新增與編輯（列內表單）、刪除、全部清除。
/// 內容經由 <see cref="IMemoryStore"/>。模型在對話中存了記憶時，<see cref="IMemoryStore.ChangeToken"/> 會換新，這裡就重新載入；
/// 重新載入不會丟掉正在輸入的表單（只有編輯中的那則已經不存在時才關閉表單）。
/// 沒有草稿：按「儲存」才寫入，「取消」就丟掉輸入。
/// </summary>
public sealed partial class MemoryViewModel : ObservableObject, IDisposable
{
    private enum FormMode { None, New, Edit }

    private readonly IMemoryStore _store;
    private readonly IModelCatalog _catalog;
    private readonly IConfirmService _confirm;
    private readonly IUiDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly MemoryOptions _options;
    private readonly List<MemoryItem> _items = [];
    private CancellationTokenRegistration _registration;
    private bool _enabled;
    private FormMode _formMode;
    private int _editingId;
    private int _generation;
    private bool _disposed;

    /// <param name="options">上限與觸發詞；沒有時用預設值（DI 沒有註冊時）。</param>
    public MemoryViewModel(IMemoryStore store, IModelCatalog catalog, IConfirmService confirm, IUiDispatcher dispatcher, TimeProvider time,
        MemoryOptions? options = null)
    {
        _store = store;
        _catalog = catalog;
        _confirm = confirm;
        _dispatcher = dispatcher;
        _time = time;
        _options = options ?? new MemoryOptions();
        _enabled = catalog.Current.Memory.Enabled;
        Form = new MemoryFormViewModel(_options, SaveAsync, CloseForm);
        catalog.Changed += OnCatalogChanged;
        Subscribe();
        _ = ReloadAsync();
    }

    /// <summary>表單（新增或編輯共用一個）。</summary>
    public MemoryFormViewModel Form { get; }

    /// <summary>清單：依分類排序，空的分類不出現。</summary>
    public ObservableCollection<MemoryGroupViewModel> Groups { get; } = [];

    /// <summary>新增表單是否開著（開著時顯示在清單上方）。</summary>
    public MemoryFormViewModel? NewForm => _formMode == FormMode.New ? Form : null;

    public bool HasItems => _items.Count > 0;

    /// <summary>已儲存的記憶總字數（字形叢集，和上限的計算方式一致）。</summary>
    public int CharacterCount => _items.Sum(i => MemoryRules.Length(i.Content));

    /// <summary>用量文字，例如「12／100 則 · 1,240／2,000 字」。</summary>
    public string UsageText => string.Format(CultureInfo.CurrentCulture, Strings.Memory_Usage,
        _items.Count.ToString("N0", CultureInfo.InvariantCulture), _options.MaxItems.ToString("N0", CultureInfo.InvariantCulture),
        CharacterCount.ToString("N0", CultureInfo.InvariantCulture), _options.MaxTotalLength.ToString("N0", CultureInfo.InvariantCulture));

    /// <summary>用量達 90% 以上（則數或字數任一）：畫面換 Warning 色，並另外寫出「接近上限」。</summary>
    public bool IsNearLimit => _items.Count * 10 >= _options.MaxItems * 9 || CharacterCount * 10 >= _options.MaxTotalLength * 9;

    /// <summary>記憶的總開關（立即套用）。</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled)
                return;
            _ = ApplyEnabledAsync(value);
        }
    }

    /// <summary>關閉時寫在開關旁邊的說明；開啟時是 null。</summary>
    public string? EnabledNote => _enabled ? null : Strings.Memory_DisabledNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public bool HasMessage => Message is not null;

    /// <summary>刪除全部：沒有記憶時停用。</summary>
    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private async Task ClearAllAsync()
    {
        var count = _items.Count;
        var confirmed = await _confirm.ConfirmDestructiveAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Memory_ClearAllQuestion, count), Strings.Memory_ClearAll);
        if (!confirmed)
            return;
        var removed = await _store.DeleteAllAsync();
        CloseForm();
        SetMessage(string.Format(CultureInfo.CurrentCulture, Strings.Memory_Cleared, removed), isError: false);
        await ReloadAsync();
    }

    private bool CanClearAll() => _items.Count > 0;

    /// <summary>標題列的主要動作：在清單上方打開新增表單。</summary>
    [RelayCommand]
    private void New()
    {
        Message = null;
        _formMode = FormMode.New;
        Form.Open(Strings.Memory_FormNewTitle, "", MemoryCategory.Profile, null);
        ApplyFormState();
    }

    /// <summary>重新載入全部記憶並重建清單。較晚開始的載入會讓較早的結果作廢。</summary>
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        try
        {
            var items = await _store.ListAsync();
            if (_disposed || generation != _generation)
                return;
            Rebuild(items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_disposed)
                SetMessage(Strings.Memory_LoadFailed, isError: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _registration.Dispose();
        _catalog.Changed -= OnCatalogChanged;
    }

    /// <summary>
    /// 先取得權杖、之後才讀資料（<see cref="ReloadAsync"/>），變動就不會漏掉。權杖被取消時回到 UI 執行緒重新載入，並換成新的權杖。
    /// </summary>
    private void Subscribe()
    {
        var registration = _store.ChangeToken.Register(() => _dispatcher.Post(OnStoreChanged));
        _registration.Dispose();
        _registration = registration;
    }

    private void OnStoreChanged()
    {
        if (_disposed)
            return;
        Subscribe();
        _ = ReloadAsync();
    }

    private void OnCatalogChanged(object? sender, AppSettings settings) =>
        _dispatcher.Post(() =>
        {
            if (!_disposed)
                SetEnabled(settings.Memory.Enabled);
        });

    private async Task ApplyEnabledAsync(bool enabled)
    {
        Message = null;
        try
        {
            await _catalog.SetMemoryEnabledAsync(enabled);
            SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetMessage(ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed, isError: true);
            OnPropertyChanged(nameof(IsEnabled));   // 沒有存成功：開關回到原來的值
        }
    }

    private void SetEnabled(bool value)
    {
        if (_enabled == value)
            return;
        _enabled = value;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(EnabledNote));
    }

    private void Rebuild(IReadOnlyList<MemoryItem> items)
    {
        _items.Clear();
        _items.AddRange(items);

        // 編輯中的那則已經不存在（例如模型或別處刪掉了）：關閉表單，說明一下
        if (_formMode == FormMode.Edit && _items.All(i => i.Id != _editingId))
        {
            _formMode = FormMode.None;
            SetMessage(Strings.Memory_Gone, isError: false);
        }

        var today = Today();
        Groups.Clear();
        foreach (var category in Enum.GetValues<MemoryCategory>())
        {
            var rows = _items.Where(i => i.Category == category).OrderBy(i => i.Id)
                .Select(i => new MemoryItemViewModel(i, today, BeginEdit, DeleteAsync))
                .ToList();
            if (rows.Count > 0)
                Groups.Add(new MemoryGroupViewModel(category, MemoryRules.CategoryName(category), rows));
        }

        ApplyFormState();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CharacterCount));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(IsNearLimit));
        ClearAllCommand.NotifyCanExecuteChanged();
    }

    /// <summary>把表單掛到正確的位置：新增在清單上方，編輯掛在那一列裡；其他列沒有表單。</summary>
    private void ApplyFormState()
    {
        foreach (var row in Groups.SelectMany(g => g.Items))
            row.EditForm = _formMode == FormMode.Edit && row.Id == _editingId ? Form : null;
        OnPropertyChanged(nameof(NewForm));
    }

    private void BeginEdit(MemoryItemViewModel row)
    {
        Message = null;
        _formMode = FormMode.Edit;
        _editingId = row.Id;
        Form.Open(string.Format(CultureInfo.CurrentCulture, Strings.Memory_FormEditTitle, row.IdText), row.Content, row.Category, row.ExpiresAt);
        ApplyFormState();
    }

    private void CloseForm()
    {
        _formMode = FormMode.None;
        Form.FormError = null;
        ApplyFormState();
    }

    /// <summary>儲存表單：新增走 <see cref="IMemoryStore.AddAsync"/>（來源是手動新增），編輯走 <see cref="IMemoryStore.UpdateAsync"/>（使用者親手輸入，略過機密樣式檢查）。</summary>
    private async Task SaveAsync()
    {
        try
        {
            if (_formMode == FormMode.New)
                await AddAsync();
            else if (_formMode == FormMode.Edit)
                await UpdateAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Form.FormError = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
        }
    }

    private async Task AddAsync()
    {
        var result = await _store.AddAsync(new NewMemory(Form.Content, Form.SelectedCategory.Category, MemorySource.Manual, null, Form.ExpiresDate));
        if (result.Succeeded)
        {
            CloseForm();
            SetMessage(Strings.Memory_Saved, isError: false);
            await ReloadAsync();
            return;
        }
        Form.FormError = result.Outcome == MemoryAddOutcome.Duplicate && result.Item is { } existing
            ? DuplicateText(existing.Id)
            : result.Message ?? Strings.Settings_SaveFailed;
    }

    private async Task UpdateAsync()
    {
        var id = _editingId;
        var result = await _store.UpdateAsync(id, new MemoryPatch(
            Content: Form.Content,
            Category: Form.SelectedCategory.Category,
            ExpiresAt: Form.ExpiresDate,
            ClearExpiresAt: Form.ExpiresDate is null,
            SkipSecretCheck: true));
        switch (result.Outcome)
        {
            case MemoryUpdateOutcome.Updated:
                CloseForm();
                SetMessage(Strings.Memory_Saved, isError: false);
                await ReloadAsync();
                break;
            case MemoryUpdateOutcome.NotFound:
                CloseForm();
                SetMessage(Strings.Memory_Gone, isError: true);
                await ReloadAsync();
                break;
            case MemoryUpdateOutcome.Duplicate when result.Item is { } existing:
                Form.FormError = DuplicateText(existing.Id);
                break;
            default:
                Form.FormError = result.Message ?? Strings.Settings_SaveFailed;
                break;
        }
    }

    private async Task DeleteAsync(MemoryItemViewModel row)
    {
        var confirmed = await _confirm.ConfirmDestructiveAsync($"{Strings.Memory_DeleteQuestion}\n{row.Content}", Strings.Memory_Forget);
        if (!confirmed)
            return;
        await _store.DeleteAsync(row.Id);
        if (_formMode == FormMode.Edit && _editingId == row.Id)
            CloseForm();
        SetMessage(Strings.Memory_Forgotten, isError: false);
        await ReloadAsync();
    }

    private static string DuplicateText(int id) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Memory_Duplicate, MemoryRules.FormatId(id));

    private DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private void SetMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}
