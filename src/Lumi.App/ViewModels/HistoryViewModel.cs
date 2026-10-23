using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lumi.App.ViewModels;

/// <summary>日期分組的五個區段，依序顯示；空的區段不顯示。</summary>
public enum HistoryBucket
{
    Today,
    Yesterday,
    Last7Days,
    Last30Days,
    Older,
}

/// <summary>清單中的群組標題列（只顯示，不能選取）。搜尋結果的「N 筆結果」也用這個。</summary>
public sealed record HistoryGroupHeader(string Title);

/// <summary>一個群組：標題（搜尋時為 null，表示不分組）與裡面的對話。</summary>
public sealed record HistoryGroup(string? Title, IReadOnlyList<ConversationListItem> Items);

/// <summary>日期分組的純邏輯（不碰畫面），方便測試午夜與時區的邊界。</summary>
public static class HistoryGrouping
{
    /// <summary>
    /// 依本地日期比較：同一天＝今天，前一天＝昨天，之後 2～7 天＝過去 7 天，8～30 天＝過去 30 天，其他＝更早。
    /// 未來的時間（時鐘校正的誤差）歸入今天。
    /// </summary>
    public static HistoryBucket BucketOf(DateTimeOffset updatedAt, DateTimeOffset now, TimeZoneInfo zone)
    {
        var day = TimeZoneInfo.ConvertTime(updatedAt, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var days = (today - day).Days;
        return days <= 0 ? HistoryBucket.Today
            : days == 1 ? HistoryBucket.Yesterday
            : days <= 7 ? HistoryBucket.Last7Days
            : days <= 30 ? HistoryBucket.Last30Days
            : HistoryBucket.Older;
    }

    public static string TitleOf(HistoryBucket bucket) => bucket switch
    {
        HistoryBucket.Today => Strings.History_GroupToday,
        HistoryBucket.Yesterday => Strings.History_GroupYesterday,
        HistoryBucket.Last7Days => Strings.History_GroupLast7,
        HistoryBucket.Last30Days => Strings.History_GroupLast30,
        _ => Strings.History_GroupOlder,
    };
}

/// <summary>
/// 歷史對話抽屜：分組清單、搜尋（節流、舊查詢取消）、載入更多（游標分頁）、改名、刪除。
/// 工作台的動作（切換對話、目前對話被刪、標題同步）經 <see cref="IHistoryHost"/> 交給工作台。
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    private readonly IConversationStore _store;
    private readonly IHistoryHost _host;
    private readonly TimeProvider _time;
    private readonly HistoryOptions _options;
    private readonly ConversationDeletionService? _deletion;
    private readonly IConfirmService? _confirm;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly Dictionary<string, Conversation> _byId = new(StringComparer.Ordinal);
    private ConversationListItem? _selected;
    private ConversationListItem? _editing;
    private string? _currentId;
    private bool _refreshing;
    private bool _loadingMore;
    private int _generation;
    private ConversationCursor? _cursor;
    private CancellationTokenSource? _searchCts;

    public HistoryViewModel(IConversationStore store, IHistoryHost host, TimeProvider time, HistoryOptions? options = null,
        ConversationDeletionService? deletion = null, IConfirmService? confirm = null, ILogger<HistoryViewModel>? logger = null)
    {
        _store = store;
        _host = host;
        _time = time;
        _options = options ?? HistoryOptions.Default;
        _deletion = deletion;
        _confirm = confirm;
        _logger = logger ?? NullLogger<HistoryViewModel>.Instance;
    }

    /// <summary>目前列出的對話（browse 時是已載入的各頁，搜尋時是命中的結果），依清單順序。</summary>
    public ObservableCollection<ConversationListItem> Items { get; } = [];

    /// <summary>分組（browse 時依日期；搜尋時只有一組、標題是「N 筆結果」）。</summary>
    public ObservableCollection<HistoryGroup> Groups { get; } = [];

    /// <summary>畫面清單的實際列：群組標題列與對話列交錯排列（由 <see cref="Groups"/> 攤平）。</summary>
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    /// <summary>搜尋框的文字。去頭尾空白後是空的就是瀏覽模式（分組清單）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchMode), nameof(IsEmpty), nameof(ShowNoResults), nameof(NoResultsText))]
    private string _searchText = "";

    /// <summary>搜尋模式：搜尋框有文字（不分組，顯示結果）。</summary>
    public bool IsSearchMode => SearchText.Trim().Length > 0;

    /// <summary>節流中或等待查詢結果。新的輸入會取消舊的查詢，舊結果不會蓋過新的。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowNoResults))]
    private bool _isSearching;

    /// <summary>還有下一頁（「載入更多」按鈕）。搜尋模式下不分頁，永遠是 false。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMore;

    /// <summary>瀏覽模式下一則對話都沒有（「還沒有對話紀錄」）。</summary>
    public bool IsEmpty => Items.Count == 0 && !IsSearchMode;

    /// <summary>搜尋完成、沒有結果（「沒有符合『…』的對話」）。</summary>
    public bool ShowNoResults => IsSearchMode && !IsSearching && Items.Count == 0;

    public string NoResultsText => string.Format(CultureInfo.CurrentCulture, Strings.History_NoResultsFormat, SearchText.Trim());

    /// <summary>清單裡目前選中的項目＝目前開著的對話；使用者點了別的就切換。</summary>
    public ConversationListItem? SelectedItem
    {
        get => _selected;
        set
        {
            if (_refreshing || value is null || ReferenceEquals(value, _selected))
                return;
            _selected = value;
            OnPropertyChanged();
            if (_byId.TryGetValue(value.Id, out var conversation))
                Observe(_host.OpenAsync(conversation));
        }
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    /// <summary>
    /// 重新載入第一頁（切換對話、送出、刪除之後）。搜尋模式下只更新「目前對話」的標記，不重查。
    /// </summary>
    public async Task RefreshAsync(string? currentId)
    {
        _currentId = currentId;
        if (IsSearchMode)
        {
            foreach (var item in Items)
                item.IsCurrent = item.Id == currentId;
            RebuildGroups();
            return;
        }

        var generation = ++_generation;
        var page = await _store.ListAsync(_options.PageSize);
        if (generation != _generation || IsSearchMode)
            return;   // 等待期間又重新載入或開始搜尋了：丟掉這次的結果

        CancelEditing();
        _byId.Clear();
        Items.Clear();
        foreach (var conversation in page)
            AddItem(conversation, "", "");
        _cursor = page.Count > 0 ? CursorOf(page[^1]) : null;
        HasMore = page.Count == _options.PageSize;
        RebuildGroups();
    }

    /// <summary>「N 筆結果」的上限：達到搜尋上限就顯示「只顯示前 N 筆」。</summary>
    private int SearchLimit => _options.SearchLimit;

    /// <summary>改名輸入框的字數上限（畫面的 MaxLength 用，送出時 <see cref="NormalizeTitle"/> 也會截斷）。</summary>
    public int TitleMaxLength => _options.TitleMaxLength;

    // ---- 分頁 ----

    private bool CanLoadMore() => HasMore && !_loadingMore && !IsSearchMode && _cursor is not null;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_cursor is not { } cursor)
            return;
        var generation = _generation;
        _loadingMore = true;
        LoadMoreCommand.NotifyCanExecuteChanged();
        try
        {
            var page = await _store.ListAsync(_options.PageSize, cursor);
            if (generation != _generation || IsSearchMode)
                return;
            foreach (var conversation in page)
            {
                if (!_byId.ContainsKey(conversation.Id))   // 游標分頁不會重複，這裡是保險
                    AddItem(conversation, "", "");
            }
            if (page.Count > 0)
                _cursor = CursorOf(page[^1]);
            HasMore = page.Count == _options.PageSize;
            RebuildGroups();
        }
        finally
        {
            _loadingMore = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- 搜尋 ----

    /// <summary>每次輸入都重新排程：取消前一次的查詢，停止輸入 <see cref="HistoryOptions.SearchDebounceMilliseconds"/> 後才查。</summary>
    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = null;
        var query = value.Trim();
        if (query.Length == 0)
        {
            IsSearching = false;
            Observe(RefreshAsync(_currentId));   // 回到分組清單（第一頁）
            return;
        }

        HasMore = false;   // 搜尋模式不分頁（結果出來前也先隱藏「載入更多」）
        IsSearching = true;
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        Observe(RunSearchAsync(query, cts.Token));
    }

    private async Task RunSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_options.SearchDebounceMilliseconds), _time, ct);
            var hits = await _store.SearchAsync(query, _options.SearchLimit, ct);
            ct.ThrowIfCancellationRequested();
            ApplySearch(query, hits);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 已經有更新的查詢（或清除了搜尋）：這次的結果不用了
        }
        catch
        {
            if (!ct.IsCancellationRequested)
                IsSearching = false;
            throw;
        }
    }

    private void ApplySearch(string query, IReadOnlyList<ConversationSearchHit> hits)
    {
        CancelEditing();
        _byId.Clear();
        Items.Clear();
        foreach (var hit in hits)
            AddItem(hit.Conversation, hit.Snippet, query);
        _cursor = null;
        HasMore = false;
        IsSearching = false;
        RebuildGroups();
    }

    // ---- 改名 ----

    /// <summary>進入改名（列的「…」選單、F2）。已經在改名的另一列會先取消。</summary>
    public event EventHandler<ConversationListItem>? EditStarted;

    [RelayCommand]
    private void Rename(ConversationListItem? item)
    {
        if (item is null)
            return;
        CancelEditing();
        item.EditText = item.Title;
        item.IsEditing = true;
        _editing = item;
        EditStarted?.Invoke(this, item);
    }

    /// <summary>確認改名：去頭尾空白；空字串或沒有變動就是取消；上限 <see cref="HistoryOptions.TitleMaxLength"/> 字。</summary>
    [RelayCommand]
    private async Task CommitRenameAsync(ConversationListItem? item)
    {
        if (item is null || !item.IsEditing)
            return;
        var title = NormalizeTitle(item.EditText, _options.TitleMaxLength);
        item.IsEditing = false;
        item.EditText = "";
        if (ReferenceEquals(_editing, item))
            _editing = null;
        if (title is null || title == item.Title)
            return;

        try
        {
            await _store.RenameAsync(item.Id, title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "改名對話失敗");
            return;
        }
        item.SetTitle(title);
        if (_byId.TryGetValue(item.Id, out var conversation))
            _byId[item.Id] = conversation with { Title = title };
        _host.TitleChanged(item.Id, title);
    }

    /// <summary>放棄改名（Esc、失去焦點）。</summary>
    [RelayCommand]
    private void CancelRename(ConversationListItem? item)
    {
        if (item is null || !item.IsEditing)
            return;
        item.IsEditing = false;
        item.EditText = "";
        if (ReferenceEquals(_editing, item))
            _editing = null;
    }

    private void CancelEditing()
    {
        if (_editing is not null)
            CancelRename(_editing);
    }

    /// <summary>去頭尾空白；空字串回 null（＝取消）；超過上限就截掉多的字元。</summary>
    public static string? NormalizeTitle(string? input, int maxLength)
    {
        var trimmed = (input ?? "").Trim();
        if (trimmed.Length == 0)
            return null;
        var clipped = string.Concat(trimmed.EnumerateRunes().Take(maxLength).Select(r => r.ToString())).TrimEnd();
        return clipped.Length == 0 ? null : clipped;
    }

    // ---- 刪除 ----

    /// <summary>回應中（含小精靈回應中）不能刪除：不分是不是目前的對話，避免清附件時誤刪進行中的圖片。</summary>
    private bool CanDelete(ConversationListItem? item) => item is not null && _deletion is not null && _confirm is not null && !_host.IsResponding;

    /// <summary>
    /// 刪除前經過確認。目前開著的對話會先導向清單中的下一則（沒有就開新的空白對話），再從資料庫刪除。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(ConversationListItem? item)
    {
        if (item is null || _deletion is null || _confirm is null || _host.IsResponding)
            return;
        var message = string.Format(CultureInfo.CurrentCulture, Strings.History_DeleteConfirmFormat, item.Title);
        if (!await _confirm.ConfirmDestructiveAsync(message, Strings.Confirm_Delete))
            return;
        // 確認期間可能開始回應或列表已經換過了
        if (_host.IsResponding || !Items.Contains(item))
            return;

        var id = item.Id;
        var next = id == _host.CurrentConversationId ? NextAfter(item) : null;
        try
        {
            await _deletion.DeleteAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "刪除對話失敗");
            return;
        }

        if (item.IsEditing)
            CancelRename(item);
        if (ReferenceEquals(_selected, item))
            _selected = null;
        Items.Remove(item);
        _byId.Remove(id);
        RebuildGroups();
        await _host.ConversationDeletedAsync(id, next);
    }

    /// <summary>清單中緊接在後面的對話（目前清單的順序）；沒有就是 null。</summary>
    private Conversation? NextAfter(ConversationListItem item)
    {
        var index = Items.IndexOf(item);
        return index >= 0 && index + 1 < Items.Count && _byId.TryGetValue(Items[index + 1].Id, out var next) ? next : null;
    }

    /// <summary>工作台的回應狀態變了：更新「不能刪除」的標記與命令的可用狀態。</summary>
    public void HostStateChanged()
    {
        foreach (var item in Items)
            item.DeleteBlocked = _host.IsResponding;
        DeleteCommand.NotifyCanExecuteChanged();
    }

    // ---- 分組與列 ----

    private void AddItem(Conversation conversation, string snippet, string query)
    {
        _byId[conversation.Id] = conversation;
        Items.Add(new ConversationListItem(conversation.Id, conversation.Title, FormatTime(conversation.UpdatedAt),
            conversation.Id == _currentId, snippet, query)
        {
            DeleteBlocked = _host.IsResponding,
            TitleMaxLength = _options.TitleMaxLength,
        });
    }

    private void RebuildGroups()
    {
        _refreshing = true;   // 重建清單會清掉 ListBox 的選取，這不是使用者切換對話
        try
        {
            Groups.Clear();
            Rows.Clear();
            _selected = Items.FirstOrDefault(i => i.IsCurrent);   // 目前的對話就是清單的選取（不會因此開啟它）
            if (IsSearchMode)
            {
                if (Items.Count > 0)
                {
                    var title = Items.Count >= SearchLimit
                        ? string.Format(CultureInfo.CurrentCulture, Strings.History_ResultsCappedFormat, SearchLimit)
                        : string.Format(CultureInfo.CurrentCulture, Strings.History_ResultsFormat, Items.Count);
                    AddGroup(title, Items);
                }
            }
            else
            {
                var now = _time.GetUtcNow();
                var zone = _time.LocalTimeZone;
                foreach (var bucket in Enum.GetValues<HistoryBucket>())
                {
                    var members = Items.Where(i => _byId.TryGetValue(i.Id, out var c)
                                                  && HistoryGrouping.BucketOf(c.UpdatedAt, now, zone) == bucket).ToList();
                    if (members.Count > 0)
                        AddGroup(HistoryGrouping.TitleOf(bucket), members);
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowNoResults));
        OnPropertyChanged(nameof(NoResultsText));
        OnPropertyChanged(nameof(SelectedItem));   // 清單重建後把目前對話重新選回去
    }

    private void AddGroup(string? title, IReadOnlyList<ConversationListItem> members)
    {
        Groups.Add(new HistoryGroup(title, members));
        if (title is not null)
            Rows.Add(new HistoryGroupHeader(title));
        foreach (var member in members)
            Rows.Add(member);
    }

    private static ConversationCursor CursorOf(Conversation conversation) => new(conversation.UpdatedAt, conversation.Id);

    /// <summary>今天顯示 <c>HH:mm</c>，其他顯示 <c>M/d</c>。</summary>
    public string FormatTime(DateTimeOffset time)
    {
        var now = _time.GetLocalNow();
        var local = TimeZoneInfo.ConvertTime(time, _time.LocalTimeZone);
        return local.Date == now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("M/d", CultureInfo.InvariantCulture);
    }

    /// <summary>背景作業的例外寫進 log（fire-and-forget 的地方一律經過這裡）。</summary>
    private void Observe(Task task) =>
        task.ContinueWith(t => _logger.LogError(t.Exception, "歷史抽屜的背景作業失敗"),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    partial void OnIsSearchingChanged(bool value) => RebuildGroups();

    /// <summary>每次開啟都重新載入第一頁（啟動後第一次開啟時清單還是空的，所以不能只靠切換或送出時的重新載入）。</summary>
    partial void OnIsOpenChanged(bool value)
    {
        if (value)
            Observe(RefreshAsync(_host.CurrentConversationId));
    }
}
