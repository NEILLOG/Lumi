using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Chat;
using Lumi.Core.Context;

namespace Lumi.App.ViewModels;

/// <summary>
/// 輸入框上方的上下文列（ui-spec 6.1）：目前這則對話的檔案、資料夾、圖片 chip，加入與移除，預估 token。
/// 命令都在 UI 執行緒上執行；檔案系統的檢查都經過 <see cref="IContextInspector"/>。
/// 回應中不能加入或移除（<see cref="IsLocked"/>）：範圍不能在工具執行到一半時改變。
/// </summary>
public partial class ContextBarViewModel : ObservableObject
{
    private readonly IConversationContextStore _store;
    private readonly IConversationStore _conversations;
    private readonly IContextInspector _inspector;
    private readonly IContextAttachmentBuilder _builder;
    private readonly ITokenEstimator _estimator;
    private readonly IFilePickerService _picker;
    private readonly IClipboardImageService _clipboard;
    private readonly IAttachmentStore _attachments;
    private readonly TimeProvider _time;

    private string? _conversationId;
    private int _historyTokens;
    private int _pendingTokens;
    private int _draftTokens;
    private int _loadVersion;

    public ContextBarViewModel(
        IConversationContextStore store,
        IConversationStore conversations,
        IContextInspector inspector,
        IContextAttachmentBuilder builder,
        ITokenEstimator estimator,
        IFilePickerService picker,
        IClipboardImageService clipboard,
        IAttachmentStore attachments,
        TimeProvider time)
    {
        _store = store;
        _conversations = conversations;
        _inspector = inspector;
        _builder = builder;
        _estimator = estimator;
        _picker = picker;
        _clipboard = clipboard;
        _attachments = attachments;
        _time = time;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>什麼都不做的實例（沒有上下文支援的組合，例如只測工作台的測試）。</summary>
    public static ContextBarViewModel CreateInert(IConversationStore conversations, TimeProvider time) => new(
        new NullConversationContextStore(), conversations, new NullContextInspector(), new NullContextAttachmentBuilder(),
        new HeuristicTokenEstimator(), new NullFilePickerService(), new NullClipboardImageService(), new NullAttachmentStore(), time);

    public ObservableCollection<ContextChipViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>回應中：不能加入或移除。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(AddFileCommand), nameof(AddFolderCommand), nameof(RemoveCommand))]
    private bool _isLocked;

    public bool CanChange => !IsLocked && _conversationId is not null;

    /// <summary>目前模型的上下文上限（token），0 表示不知道。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverLimit), nameof(OverLimitText))]
    private int _contextWindow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokensText), nameof(IsOverLimit))]
    private int _estimatedTokens;

    /// <summary>一次性的說明（例如移除已帶入的項目）；下一次加入、移除或切換對話時清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? _notice;

    /// <summary>加入失敗的原因（找不到…）；下一次操作時清掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasNotice => Notice is not null;

    public bool HasError => Error is not null;

    public string TokensText => string.Format(CultureInfo.CurrentCulture, Strings.Context_Tokens, WorkbenchViewModel.FormatTokens(EstimatedTokens));

    public bool IsOverLimit => ContextWindow > 0 && EstimatedTokens > ContextWindow;

    public string OverLimitText => string.Format(CultureInfo.CurrentCulture, Strings.Context_TokensOver, WorkbenchViewModel.FormatTokens(ContextWindow));

    // ---- 載入 ----

    /// <summary>切換到某則對話（null＝沒有對話）：重新讀取項目、檢查狀態、估算。</summary>
    public async Task LoadAsync(string? conversationId)
    {
        if (conversationId != _conversationId)
        {
            Notice = null;
            Error = null;
        }
        _conversationId = conversationId;
        OnPropertyChanged(nameof(CanChange));
        AddFileCommand.NotifyCanExecuteChanged();
        AddFolderCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
        await RefreshAsync();
    }

    /// <summary>重新讀取目前的項目與估算（送出之後、歷史重讀之後）。</summary>
    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        var conversationId = _conversationId;
        if (conversationId is null)
        {
            Items.Clear();
            _historyTokens = _pendingTokens = 0;
            UpdateEstimate();
            return;
        }

        var items = await _store.GetAsync(conversationId);
        var chips = new List<ContextChipViewModel>();
        foreach (var item in items)
            chips.Add(new ContextChipViewModel(item, await _inspector.InspectAsync(item.Kind, item.Path)));

        var history = await _conversations.GetMessagesAsync(conversationId);
        var historyTokens = _estimator.EstimateMessages(ConversationService.BuildRequestMessages(history));
        var pendingTokens = await _builder.EstimateTokensAsync(items.Where(i => i.IsPending).ToList());

        // 切換對話或又重新載入了：丟掉這次過時的結果
        if (version != _loadVersion || conversationId != _conversationId)
            return;

        Items.Clear();
        foreach (var chip in chips)
            Items.Add(chip);
        _historyTokens = historyTokens;
        _pendingTokens = pendingTokens;
        UpdateEstimate();
    }

    /// <summary>輸入框的草稿變了：只更新草稿的那一部分（便宜），不重讀檔案。</summary>
    public void UpdateDraft(string text)
    {
        _draftTokens = _estimator.Estimate(text);
        UpdateEstimate();
    }

    private void UpdateEstimate() => EstimatedTokens = _historyTokens + _pendingTokens + _draftTokens;

    /// <summary>目前待送出的項目（送出時用來在使用者氣泡上先顯示 chip）。</summary>
    public IReadOnlyList<ContextChipViewModel> PendingChips() => Items.Where(c => !c.IsSent).ToList();

    // ---- 加入 ----

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task AddFileAsync()
    {
        var paths = await _picker.PickFilesAsync();
        if (paths.Count > 0)
            await AddPathsAsync(paths);
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task AddFolderAsync()
    {
        if (await _picker.PickFolderAsync() is { } path)
            await AddPathsAsync([path]);
    }

    /// <summary>
    /// 選擇器、拖放、貼上共用的入口。資料夾、圖片、檔案由內容判斷；找不到的路徑列在 <see cref="Error"/>，其餘照常加入。
    /// 回傳實際加入（或重新加入）了幾個。
    /// </summary>
    public async Task<int> AddPathsAsync(IEnumerable<string> paths, string? displayNameOverride = null)
    {
        if (!CanChange || _conversationId is not { } conversationId)
            return 0;

        Notice = null;
        Error = null;
        var added = 0;
        var missing = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            var kind = await _inspector.ClassifyAsync(path);
            if (kind is null)
            {
                missing.Add(System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : path);
                continue;
            }

            var displayName = displayNameOverride ?? (System.IO.Path.GetFileName(path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : path);
            await _store.AddAsync(conversationId, kind.Value, path, displayName);
            added++;
        }

        if (missing.Count > 0)
            Error = string.Format(CultureInfo.CurrentCulture, Strings.Context_NotFoundFormat, string.Join("、", missing));
        if (added > 0)
            await RefreshAsync();
        return added;
    }

    /// <summary>貼上截圖：剪貼簿有圖片就存進附件資料夾、當成圖片項目加入。沒有圖片回 false（交給一般的貼上）。</summary>
    public async Task<bool> PasteImageAsync()
    {
        if (!CanChange || await _clipboard.TryGetImageAsync() is not { } image)
            return false;

        string? path;
        try
        {
            var fileName = await _attachments.SaveAsync(image.Data, image.MediaType);
            path = _attachments.GetPath(fileName);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Error = Strings.Context_ImageUnsupported;
            return true;
        }

        if (path is null)
            return false;

        var name = string.Format(CultureInfo.CurrentCulture, Strings.Context_PastedImageName,
            _time.GetLocalNow().ToString("HH:mm", CultureInfo.InvariantCulture));
        await AddPathsAsync([path], name);
        return true;
    }

    // ---- 移除 ----

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task RemoveAsync(ContextChipViewModel? chip)
    {
        if (chip is null || _conversationId is not { } conversationId)
            return;

        Notice = null;
        Error = null;
        await _store.RemoveAsync(conversationId, chip.Id);
        // 已經帶進過訊息的項目：從範圍拿掉了，但模型之前看過的內容收不回來，要讓使用者知道
        var wasSent = chip.IsSent;
        await RefreshAsync();
        if (wasSent)
            Notice = Strings.Context_RemovedSentNote;
    }
}
