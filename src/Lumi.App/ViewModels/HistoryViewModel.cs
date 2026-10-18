using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Core.Chat;

namespace Lumi.App.ViewModels;

public sealed record ConversationListItem(string Id, string Title, string TimeText, bool IsCurrent);

/// <summary>
/// 歷史對話抽屜（M2 最簡版）：依最近更新時間列出有訊息的對話，點了切換。
/// 搜尋、刪除、改名、依日期分組是 M9。
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    public const int Limit = 100;

    private readonly IConversationStore _store;
    private readonly Func<Conversation, Task> _open;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Conversation> _byId = new(StringComparer.Ordinal);
    private ConversationListItem? _selected;
    private bool _refreshing;

    public HistoryViewModel(IConversationStore store, Func<Conversation, Task> open, TimeProvider time)
    {
        _store = store;
        _open = open;
        _time = time;
    }

    public ObservableCollection<ConversationListItem> Items { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    public bool IsEmpty => Items.Count == 0;

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
                _ = _open(conversation);
        }
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    public async Task RefreshAsync(string? currentId)
    {
        var conversations = await _store.ListAsync(Limit);
        _refreshing = true;
        try
        {
            _byId.Clear();
            Items.Clear();
            foreach (var c in conversations)
            {
                _byId[c.Id] = c;
                Items.Add(new ConversationListItem(c.Id, c.Title, FormatTime(c.UpdatedAt), c.Id == currentId));
            }
            _selected = Items.FirstOrDefault(i => i.IsCurrent);
            OnPropertyChanged(nameof(SelectedItem));
            OnPropertyChanged(nameof(IsEmpty));
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>今天顯示 <c>HH:mm</c>，其他顯示 <c>M/d</c>。</summary>
    public string FormatTime(DateTimeOffset time)
    {
        var now = _time.GetLocalNow();
        var local = TimeZoneInfo.ConvertTime(time, _time.LocalTimeZone);
        return local.Date == now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("M/d", CultureInfo.InvariantCulture);
    }
}
