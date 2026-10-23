using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>標題的一段：<see cref="Hit"/> 為 true 的段落是搜尋命中的字，畫面上加粗。</summary>
public sealed record TitlePart(string Text, bool Hit);

/// <summary>歷史抽屜的一列（對話）。標題、改名中的編輯狀態與刪除是否被擋住都在這裡，畫面只負責顯示。</summary>
public partial class ConversationListItem : ObservableObject
{
    private string _query = "";

    public ConversationListItem(string id, string title, string timeText, bool isCurrent, string snippet = "", string query = "")
    {
        Id = id;
        _title = title;
        TimeText = timeText;
        _isCurrent = isCurrent;
        _snippet = snippet;
        ApplyQuery(query);
    }

    public string Id { get; }

    /// <summary>改名輸入框的字數上限（畫面的 MaxLength 綁定這個值）。</summary>
    public int TitleMaxLength { get; init; }

    /// <summary>已格式化的時間文字（今天 HH:mm，其他 M/d）。</summary>
    public string TimeText { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private IReadOnlyList<TitlePart> _titleSegments = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnippet))]
    private string _snippet;

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>改名中：標題換成輸入框（<see cref="EditText"/>）。一次只有一列能改名。</summary>
    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    /// <summary>回應中：這一列不能刪除（<see cref="DeleteTip"/> 寫出原因）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteTip))]
    private bool _deleteBlocked;

    public bool HasSnippet => Snippet.Length > 0;

    public string DeleteTip => DeleteBlocked ? Strings.History_DeleteBlocked : "";

    /// <summary>設定搜尋字串，重算標題的命中段落。空字串＝沒有命中。</summary>
    public void ApplyQuery(string query)
    {
        _query = query;
        TitleSegments = Highlight(Title, query);
    }

    /// <summary>改名後呼叫：標題換新，命中段落依目前的搜尋字串重算。</summary>
    public void SetTitle(string title)
    {
        Title = title;
        TitleSegments = Highlight(title, _query);
    }

    /// <summary>不分大小寫的每一處命中都標成 Hit；沒有命中就是單一段落。</summary>
    public static IReadOnlyList<TitlePart> Highlight(string title, string query)
    {
        var parts = new List<TitlePart>();
        var pos = 0;
        if (query.Length > 0)
        {
            while (true)
            {
                var index = title.IndexOf(query, pos, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;
                if (index > pos)
                    parts.Add(new TitlePart(title[pos..index], false));
                parts.Add(new TitlePart(title.Substring(index, query.Length), true));
                pos = index + query.Length;
            }
        }
        if (pos < title.Length || parts.Count == 0)
            parts.Add(new TitlePart(title[pos..], false));
        return parts;
    }
}
