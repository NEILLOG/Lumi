using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Chat;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.ViewModels;

/// <summary>「花最多的對話」的一列（本月前 5 則）。</summary>
public sealed record UsageTopConversationItem(string ConversationId, string Title, string MetaText, string CostText);

/// <summary>
/// 設定 › 用量與費用（plan/23-usage-cost.md 第 6、7.3、8 節）。顯示用的數字與字串在這裡算好，畫面只綁定。
/// 開啟對話與跳到 AI 連線頁由整合包接上（<paramref name="openConversation"/>、<paramref name="openPricing"/>）；沒接就什麼都不做。
/// </summary>
public sealed partial class UsageSettingsViewModel : ObservableObject
{
    /// <summary>「花最多的對話」列數。</summary>
    public const int TopConversationLimit = 5;

    /// <summary>預算的警示門檻（與 <see cref="BudgetAlertLevel"/> 相同）。</summary>
    private const decimal WarningRatio = 0.8m;

    private readonly IUsageStore _usage;
    private readonly IUsageHintLog _hintLog;
    private readonly IModelCatalog _catalog;
    private readonly IConversationStore _conversations;
    private readonly IFilePickerService _files;
    private readonly TimeProvider _time;
    private readonly Func<string, Task>? _openConversation;
    private readonly Action? _openPricing;
    private int _loadGeneration;

    public UsageSettingsViewModel(IUsageStore usage, IUsageHintLog hints, IModelCatalog catalog, IConversationStore conversations,
        IFilePickerService files, TimeProvider time, Func<string, Task>? openConversation = null, Action? openPricing = null)
    {
        _usage = usage;
        _hintLog = hints;
        _catalog = catalog;
        _conversations = conversations;
        _files = files;
        _time = time;
        _openConversation = openConversation;
        _openPricing = openPricing;

        EditBudgetCommand = new RelayCommand(BeginEditBudget);
        SaveBudgetCommand = new AsyncRelayCommand(SaveBudgetAsync);
        CancelBudgetCommand = new RelayCommand(CancelEditBudget);
        ClearBudgetCommand = new AsyncRelayCommand(ClearBudgetAsync);
        OpenConversationCommand = new AsyncRelayCommand<string?>(OpenConversationAsync);
        OpenPricingCommand = new RelayCommand(() => _openPricing?.Invoke());
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        UnmuteHintCommand = new AsyncRelayCommand<string?>(UnmuteHintAsync);
        ToggleHintCommand = new RelayCommand<string?>(ToggleHint);
    }

    public IRelayCommand EditBudgetCommand { get; }

    public IAsyncRelayCommand SaveBudgetCommand { get; }

    public IRelayCommand CancelBudgetCommand { get; }

    public IAsyncRelayCommand ClearBudgetCommand { get; }

    public IAsyncRelayCommand<string?> OpenConversationCommand { get; }

    public IRelayCommand OpenPricingCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    public IAsyncRelayCommand<string?> UnmuteHintCommand { get; }

    public IRelayCommand<string?> ToggleHintCommand { get; }

    // ===== 本月卡片 =====

    /// <summary>大數字，例如 <c>NT$488</c>（有估算時前面加「≈」）。</summary>
    [ObservableProperty]
    private string _totalText = MoneyFormat.Twd(0);

    /// <summary>「10/1–10/29 · 約 US$15.25」；本月沒有紀錄時為 null。</summary>
    [ObservableProperty]
    private string? _rangeText;

    /// <summary>本月沒有任何紀錄：卡片只剩 <see cref="TotalText"/> 與說明，其餘段落不顯示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBudgetSummary), nameof(ShowSetBudgetLink), nameof(ShowBudgetEditor), nameof(ShowProjection))]
    private bool _isEmpty = true;

    /// <summary>有每月預算。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBudgetSummary), nameof(ShowSetBudgetLink))]
    private bool _hasBudget;

    /// <summary>「每月預算 NT$800 · 已用 61%」；沒設預算時為 null。</summary>
    [ObservableProperty]
    private string? _budgetText;

    /// <summary>已用比例 0–1（進度條用，超過 100% 夾在 1）。</summary>
    [ObservableProperty]
    private double _budgetRatio;

    /// <summary>進度條等級：Normal（&lt; 80%）、Warning（80～99%）、Danger（≥ 100%）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBudgetWarning), nameof(IsBudgetDanger))]
    private UsageLevel _budgetLevel = UsageLevel.Normal;

    public bool IsBudgetWarning => BudgetLevel == UsageLevel.Warning;

    public bool IsBudgetDanger => BudgetLevel == UsageLevel.Danger;

    /// <summary>預測句（月底約）；樣本不足或本月沒有紀錄時為 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProjection))]
    private string? _projectionText;

    public bool ShowProjection => ProjectionText is not null && !IsEmpty;

    /// <summary>「有 3 次請求的模型未設定單價，沒有計入。」；沒有時為 null。</summary>
    [ObservableProperty]
    private string? _unpricedText;

    // ===== 預算編輯（原地展開） =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBudgetSummary), nameof(ShowSetBudgetLink), nameof(ShowBudgetEditor))]
    private bool _isEditingBudget;

    /// <summary>新台幣整數金額的輸入文字。</summary>
    [ObservableProperty]
    private string _budgetInput = "";

    /// <summary>輸入不合法時的說明；null 表示沒有錯誤。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBudgetError))]
    private string? _budgetError;

    public bool HasBudgetError => BudgetError is not null;

    /// <summary>有預算、未在編輯：顯示金額與「編輯」。</summary>
    public bool ShowBudgetSummary => HasBudget && !IsEditingBudget;

    /// <summary>沒有預算、未在編輯：只顯示「設定每月預算」。本月還沒有用量時也要能先設定預算。</summary>
    public bool ShowSetBudgetLink => !HasBudget && !IsEditingBudget;

    /// <summary>編輯中：金額輸入、取消／儲存、不設預算。</summary>
    public bool ShowBudgetEditor => IsEditingBudget;

    // ===== 每天、花最多的對話、提示、頁尾 =====

    /// <summary>1 日到月底，每天一筆（<see cref="DailyCostBars"/> 用）。</summary>
    [ObservableProperty]
    private IReadOnlyList<DailyCost> _daily = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopConversations))]
    private IReadOnlyList<UsageTopConversationItem> _topConversations = [];

    public bool HasTopConversations => TopConversations.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHints))]
    private IReadOnlyList<UsageHintSummaryItem> _hints = [];

    public bool ShowHints => Hints.Count > 0;

    // ===== 匯出 =====

    /// <summary>匯出結果或失敗說明；還沒匯出時為 null。</summary>
    [ObservableProperty]
    private string? _exportMessage;

    [ObservableProperty]
    private bool _exportMessageIsError;

    /// <summary>頁面顯示時呼叫。較晚開始的載入讓較早的結果作廢。</summary>
    public async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var (from, to) = MonthlyUsageSummary.MonthRange(now, zone);
        var usage = _catalog.Current.Usage;

        var records = await _usage.ListAsync(from, to);
        if (generation != _loadGeneration)
            return;
        var summary = MonthlyUsageSummary.Build(records, usage.MonthlyBudgetUsd, now, zone);

        var top = await _usage.ListTopConversationsAsync(from, to, TopConversationLimit);
        if (generation != _loadGeneration)
            return;

        var hintEntries = await _hintLog.ListAsync(from, to);
        if (generation != _loadGeneration)
            return;
        var hints = await BuildHintsAsync(hintEntries, usage.Muted, zone, generation);
        if (hints is null)
            return;

        ApplySummary(summary, zone);
        TopConversations = top.Select(item => new UsageTopConversationItem(
            item.ConversationId,
            item.Title,
            string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_TopMetaFormat, Md(item.LastAt, zone), item.TurnCount.ToString(CultureInfo.InvariantCulture)),
            MoneyFormat.Twd(item.CostUsd, item.HasEstimated))).ToList();
        Hints = hints;
    }

    private void ApplySummary(MonthlyUsageSummary summary, TimeZoneInfo zone)
    {
        IsEmpty = summary.IsEmpty;
        TotalText = MoneyFormat.Twd(summary.TotalUsd, summary.HasEstimated);
        RangeText = summary.IsEmpty
            ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_RangeFormat,
                Md(summary.RangeStart), Md(summary.RangeEnd), MoneyFormat.Usd(summary.TotalUsd));
        Daily = summary.Daily;

        if (summary.BudgetUsd is { } budget && budget > 0)
        {
            // 百分比與等級用 decimal 算，避免浮點數把 60.99… 顯示成 60%、或把 99.99… 算成 100%。
            var fraction = summary.TotalUsd / budget;
            var percent = (int)Math.Floor(fraction * 100m);
            HasBudget = true;
            BudgetText = string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_BudgetFormat,
                MoneyFormat.Twd(budget), percent.ToString(CultureInfo.InvariantCulture));
            BudgetRatio = (double)Math.Min(fraction, 1m);
            BudgetLevel = fraction >= 1m ? UsageLevel.Danger : fraction >= WarningRatio ? UsageLevel.Warning : UsageLevel.Normal;
        }
        else
        {
            HasBudget = false;
            BudgetText = null;
            BudgetRatio = 0;
            BudgetLevel = UsageLevel.Normal;
        }

        ProjectionText = summary.ProjectedUsd is { } projected
            ? string.Format(CultureInfo.CurrentCulture,
                summary.WillExceed ? Strings.UsagePage_ProjectionOverFormat : Strings.UsagePage_ProjectionFormat,
                MoneyFormat.Twd(projected, summary.HasEstimated))
            : null;

        UnpricedText = summary.UnpricedCount > 0 && !summary.IsEmpty
            ? string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_UnpricedFormat, summary.UnpricedCount.ToString(CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// 把本月紀錄依規則分組：只列有紀錄的規則，依優先順序排；對話清單只列查得到標題的。回 null 表示載入已過期。
    /// </summary>
    private async Task<IReadOnlyList<UsageHintSummaryItem>?> BuildHintsAsync(IReadOnlyList<UsageHintEntry> entries, IReadOnlyList<string> muted,
        TimeZoneInfo zone, int generation)
    {
        var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
        var items = new List<UsageHintSummaryItem>();
        foreach (var rule in UsageHintIds.All)
        {
            var mine = entries.Where(e => e.Rule == rule).ToList();
            if (mine.Count == 0)
                continue;

            var withConversation = mine.Where(e => e.ConversationId is not null).ToList();
            var conversationCount = withConversation.Select(e => e.ConversationId).Distinct(StringComparer.Ordinal).Count();

            var conversations = new List<UsageHintConversationItem>();
            foreach (var group in withConversation.GroupBy(e => e.ConversationId!, StringComparer.Ordinal)
                         .OrderByDescending(g => g.Max(e => e.CreatedAt)))
            {
                var title = await TitleAsync(group.Key, titles);
                if (generation != _loadGeneration)
                    return null;
                if (title is null)
                    continue;
                conversations.Add(new UsageHintConversationItem(group.Key, title, Md(group.Max(e => e.CreatedAt), zone)));
            }

            items.Add(new UsageHintSummaryItem(rule, conversationCount, muted.Contains(rule), conversations));
        }
        return items;
    }

    private async Task<string?> TitleAsync(string conversationId, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(conversationId, out var cached))
            return cached;
        var title = (await _conversations.GetConversationAsync(conversationId))?.Title;
        cache[conversationId] = title;
        return title;
    }

    // ===== 命令 =====

    private void BeginEditBudget()
    {
        BudgetInput = _catalog.Current.Usage.MonthlyBudgetUsd is { } budget && budget > 0
            ? MoneyFormat.TwdAmount(budget).ToString("0", CultureInfo.InvariantCulture)
            : "";
        BudgetError = null;
        IsEditingBudget = true;
    }

    private void CancelEditBudget()
    {
        BudgetError = null;
        IsEditingBudget = false;
    }

    private async Task SaveBudgetAsync()
    {
        if (!int.TryParse(BudgetInput.Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var twd) || twd <= 0)
        {
            BudgetError = Strings.UsagePage_BudgetInvalid;
            return;
        }

        var usage = _catalog.Current.Usage;
        await _catalog.SetUsageAsync(usage with { MonthlyBudgetUsd = MoneyFormat.UsdFromTwd(twd) });
        BudgetError = null;
        IsEditingBudget = false;
        await LoadAsync();
    }

    private async Task ClearBudgetAsync()
    {
        var usage = _catalog.Current.Usage;
        if (usage.MonthlyBudgetUsd is not null)
            await _catalog.SetUsageAsync(usage with { MonthlyBudgetUsd = null });
        BudgetError = null;
        IsEditingBudget = false;
        await LoadAsync();
    }

    private async Task OpenConversationAsync(string? conversationId)
    {
        if (conversationId is null || _openConversation is null)
            return;
        await _openConversation(conversationId);
    }

    private void ToggleHint(string? rule)
    {
        var item = Hints.FirstOrDefault(h => h.Rule == rule);
        if (item is { HasConversations: true })
            item.IsExpanded = !item.IsExpanded;
    }

    private async Task UnmuteHintAsync(string? rule)
    {
        if (rule is null)
            return;
        var usage = _catalog.Current.Usage;
        await _catalog.SetUsageAsync(usage with { MutedHints = usage.Muted.Where(r => r != rule).ToList() });
        var item = Hints.FirstOrDefault(h => h.Rule == rule);
        if (item is not null)
            item.IsMuted = false;
    }

    /// <summary>
    /// 匯出本月明細（CSV）。檔名 <c>Lumi-用量-yyyy-MM.csv</c>；取消選擇就什麼都不做；成功或失敗都寫在頁尾。
    /// </summary>
    private async Task ExportAsync()
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var fileName = "Lumi-用量-" + local.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".csv";

        try
        {
            var path = await _files.SaveFileAsync(fileName, "csv");
            if (path is null)
                return;

            var (from, to) = MonthlyUsageSummary.MonthRange(now, zone);
            var records = await _usage.ListAsync(from, to);
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var id in records.Select(r => r.ConversationId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var conversation = await _conversations.GetConversationAsync(id);
                if (conversation is not null)
                    titles[id] = conversation.Title;
            }

            await using (var stream = File.Create(path))
                await UsageCsvExporter.WriteAsync(stream, records, titles, zone);

            ExportMessage = string.Format(CultureInfo.CurrentCulture, Strings.UsagePage_ExportDoneFormat, path);
            ExportMessageIsError = false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExportMessage = Strings.UsagePage_ExportFailed;
            ExportMessageIsError = true;
        }
    }

    /// <summary>「M/d」（本地日期）。</summary>
    private static string Md(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("M/d", CultureInfo.InvariantCulture);

    private static string Md(DateOnly date) => date.ToString("M/d", CultureInfo.InvariantCulture);
}
