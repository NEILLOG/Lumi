using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.App.Services;
using Lumi.Core.Usage;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台每次更新用量時交給 <see cref="ConversationUsageViewModel"/> 的原始數字（ui-usage-and-topbar.md 第 4 節）。
/// </summary>
/// <param name="HasData">有任何 usage（還沒有就是「尚無用量」）。</param>
/// <param name="ContextUsed">最近一次請求：輸入（含快取）＋輸出。</param>
/// <param name="ContextLimit">模型的上下文上限；0 或未知時不顯示百分比與圓環進度。</param>
/// <param name="InputTokens">本對話累計輸入（含快取讀取與寫入）。</param>
/// <param name="CacheReadTokens">本對話累計快取讀取。</param>
/// <param name="OutputTokens">本對話累計輸出。</param>
/// <param name="TurnCount">使用者送出的次數。</param>
/// <param name="ModelName">目前模型的顯示名稱。</param>
/// <param name="ConnectionName">目前連線的名稱（連線列的「已連線 · {0}」）。</param>
/// <param name="Connection">目前連線的狀態。</param>
public sealed record ConversationUsageSnapshot(
    bool HasData,
    int ContextUsed,
    int ContextLimit,
    int InputTokens,
    int CacheReadTokens,
    int OutputTokens,
    int TurnCount,
    string ModelName,
    string ConnectionName,
    ConnectionStatus Connection)
{
    public static ConversationUsageSnapshot Empty { get; } = new(false, 0, 0, 0, 0, 0, 0, "", "", ConnectionStatus.Unknown);
}

/// <summary>
/// 本對話用量（用量圖示與面板的資料來源，ui-usage-and-topbar.md 第 3、4 節）。
/// 原始數字由工作台呼叫 <see cref="Update"/> 推進來；這裡只做計算與顯示字串，UI 只綁定屬性。
/// </summary>
public sealed partial class ConversationUsageViewModel : ObservableObject
{
    private static readonly string[] ComputedProperties =
    [
        nameof(HasData), nameof(ContextUsed), nameof(ContextLimit), nameof(HasContextLimit), nameof(Ratio), nameof(Level),
        nameof(IsNormalLevel), nameof(IsWarningLevel), nameof(IsDangerLevel), nameof(IsNearLimit),
        nameof(PercentText), nameof(UsedOfText), nameof(RemainingText), nameof(StatusText), nameof(HasStatus),
        nameof(ModelName), nameof(InputText), nameof(OutputText), nameof(TurnsText),
        nameof(CacheRatio), nameof(CacheRatioText), nameof(ShowCacheRing),
        nameof(ConnectionState), nameof(ConnectionText), nameof(ShowRetry),
        nameof(IsConnectionOk), nameof(IsConnectionFailed), nameof(IsConnectionUnknown),
        nameof(RingTooltip), nameof(RingAutomationName),
    ];

    private static readonly string[] CostProperties =
    [
        nameof(ConversationCostText), nameof(MonthCostText), nameof(HasBudget), nameof(MonthRatio),
        nameof(IsMonthWarning), nameof(IsMonthDanger),
    ];

    private ConversationUsageSnapshot _state = ConversationUsageSnapshot.Empty;

    private ConversationCost _conversationCost = ConversationCost.Empty;
    private decimal _monthUsd;
    private decimal? _budgetUsd;
    private readonly Action? _openUsagePage;

    /// <param name="retry">連線列的「重試」要做的事（測試目前模型的連線）；沒有時按了也什麼都不做。</param>
    /// <param name="openUsagePage">面板的「用量與費用 ›」要做的事（開設定視窗到該分類）；沒有時按了也什麼都不做。</param>
    public ConversationUsageViewModel(Func<Task>? retry = null, Action? openUsagePage = null)
    {
        RetryCommand = new AsyncRelayCommand(retry ?? (() => Task.CompletedTask));
        _openUsagePage = openUsagePage;
    }

    /// <summary>連線列的「重試」（只在連線失敗時顯示）。</summary>
    public IAsyncRelayCommand RetryCommand { get; }

    /// <summary>工作台推進新的數字；所有計算屬性一併通知更新。</summary>
    public void Update(ConversationUsageSnapshot state)
    {
        _state = state;
        foreach (var name in ComputedProperties)
            OnPropertyChanged(name);
    }

    /// <summary>
    /// 工作台推進費用數字（本對話、本月、每月預算）。只在載入、回應結束、上下文整理完成時推進，串流中不更新。
    /// <paramref name="budgetUsd"/> 是 null 或 ≤ 0 時視為沒設預算。
    /// </summary>
    public void UpdateCost(ConversationCost conversation, decimal monthUsd, decimal? budgetUsd)
    {
        _conversationCost = conversation;
        _monthUsd = monthUsd;
        _budgetUsd = budgetUsd is > 0 ? budgetUsd : null;
        foreach (var name in CostProperties)
            OnPropertyChanged(name);
    }

    /// <summary>費用段落要不要顯示（沒有用量資料來源時整段隱藏，行為和沒有費用功能前相同）。</summary>
    [ObservableProperty]
    private bool _showCost;

    // ---- 原始數字 ----

    public bool HasData => _state.HasData;

    public int ContextUsed => _state.ContextUsed;

    public int ContextLimit => _state.ContextLimit;

    /// <summary>上下文上限已知（大於 0）。未知時不顯示百分比、圓環進度與「還可以再用」。</summary>
    public bool HasContextLimit => _state.ContextLimit > 0;

    // ---- 圓環與等級 ----

    /// <summary>已用比例（0–1）。沒有上限或沒有資料時是 0。</summary>
    public double Ratio => HasData && HasContextLimit
        ? Math.Clamp((double)_state.ContextUsed / _state.ContextLimit, 0, 1)
        : 0;

    /// <summary>
    /// 等級（決定圓環顏色）。沒有上限時一律 <see cref="UsageLevel.Empty"/>：面板只顯示累計數字，圓環畫虛線軌道。
    /// </summary>
    public UsageLevel Level => UsageLevelRules.From(HasData && HasContextLimit, Ratio);

    public bool IsNormalLevel => Level == UsageLevel.Normal;

    public bool IsWarningLevel => Level == UsageLevel.Warning;

    public bool IsDangerLevel => Level == UsageLevel.Danger;

    /// <summary>快滿（Danger）：按鈕右上紅點、面板的快滿提醒。</summary>
    public bool IsNearLimit => IsDangerLevel;

    /// <summary>
    /// 百分比文字（四捨五入到整數）。有用量但不到 1% 顯示「&lt;1%」；沒有資料是「0%」；沒有上限是空字串（不顯示）。
    /// </summary>
    public string PercentText
    {
        get
        {
            if (!HasContextLimit)
                return "";
            if (!HasData)
                return "0%";
            var percent = Ratio * 100;
            if (percent < 1 && _state.ContextUsed > 0)
                return Strings.Usage_LessThanOnePercent;
            return Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";
        }
    }

    /// <summary>「12K / 128K」；沒有上限時只有已用的數字。</summary>
    public string UsedOfText => HasContextLimit
        ? string.Format(CultureInfo.CurrentCulture, Strings.Usage_UsedOfFormat,
            WorkbenchViewModel.FormatTokens(_state.ContextUsed), WorkbenchViewModel.FormatTokens(_state.ContextLimit))
        : WorkbenchViewModel.FormatTokens(_state.ContextUsed);

    /// <summary>「還可以再用約 116K」（下限 0）；沒有上限時是空字串。</summary>
    public string RemainingText => HasContextLimit
        ? string.Format(CultureInfo.CurrentCulture, Strings.Usage_RemainingFormat,
            WorkbenchViewModel.FormatTokens(Math.Max(_state.ContextLimit - _state.ContextUsed, 0)))
        : "";

    /// <summary>狀態文字：空間充足／已用超過一半／快滿了。尚無等級時是空字串（不顯示）。</summary>
    public string StatusText => Level switch
    {
        UsageLevel.Normal => Strings.Usage_Ok,
        UsageLevel.Warning => Strings.Usage_Half,
        UsageLevel.Danger => Strings.Usage_Full,
        _ => "",
    };

    public bool HasStatus => StatusText.Length > 0;

    // ---- 面板：模型與三個小方塊 ----

    public string ModelName => _state.ModelName;

    /// <summary>輸入（含快取）。</summary>
    public string InputText => WorkbenchViewModel.FormatTokens(_state.InputTokens);

    public string OutputText => WorkbenchViewModel.FormatTokens(_state.OutputTokens);

    public string TurnsText => WorkbenchViewModel.FormatTokens(_state.TurnCount);

    /// <summary>快取命中率：累計快取讀取 ÷ 累計輸入（含快取），0–1。</summary>
    public double CacheRatio => _state.InputTokens > 0
        ? Math.Clamp((double)_state.CacheReadTokens / _state.InputTokens, 0, 1)
        : 0;

    /// <summary>「快取 N%」（整數）。</summary>
    public string CacheRatioText => string.Format(CultureInfo.CurrentCulture, Strings.Usage_CacheFormat,
        Math.Round(CacheRatio * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>累計快取讀取大於 0 才畫迷你圓環；否則（供應商沒回報快取）畫輸入箭頭、寫「tokens」。</summary>
    public bool ShowCacheRing => _state.CacheReadTokens > 0;

    // ---- 連線列 ----

    public ConnectionStatus ConnectionState => _state.Connection;

    public bool IsConnectionOk => ConnectionState == ConnectionStatus.Ok;

    public bool IsConnectionFailed => ConnectionState == ConnectionStatus.Failed;

    public bool IsConnectionUnknown => ConnectionState == ConnectionStatus.Unknown;

    /// <summary>已連線 · {連線名稱}；尚未確認；連線失敗（「重試」另外顯示）。</summary>
    public string ConnectionText => ConnectionState switch
    {
        ConnectionStatus.Ok => _state.ConnectionName.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Usage_ConnectedFormat, _state.ConnectionName)
            : Strings.Workbench_StatusConnected,
        ConnectionStatus.Failed => Strings.Usage_Failed,
        _ => Strings.Usage_Unknown,
    };

    public bool ShowRetry => IsConnectionFailed;

    // ---- 費用（M23 23H）：本對話、本月與預算；金額是估算，格式見 MoneyFormat ----

    /// <summary>「本對話」的費用：有單價就是金額（估算加「≈」）；全部沒有單價是「未設定單價」；沒有紀錄是 NT$0。</summary>
    public string ConversationCostText
    {
        get
        {
            if (_conversationCost.CostUsd is { } usd)
                return MoneyFormat.Twd(usd, _conversationCost.HasEstimated);
            return _conversationCost.UnpricedCount > 0 ? Strings.Usage_Unpriced : MoneyFormat.Twd(0);
        }
    }

    public bool HasBudget => _budgetUsd is not null;

    /// <summary>「本月」的費用；有預算時是「NT$488 ／ 800」（預算為不帶 NT$ 的整數），沒有預算時只有金額。</summary>
    public string MonthCostText
    {
        get
        {
            var month = MoneyFormat.Twd(_monthUsd);
            if (_budgetUsd is not { } budget)
                return month;
            var limit = MoneyFormat.TwdAmount(budget).ToString("N0", CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.CurrentCulture, Strings.Usage_MonthOfBudgetFormat, month, limit);
        }
    }

    /// <summary>本月已用比例（0–1，進度條用）；沒有預算是 0。</summary>
    public double MonthRatio => MonthRawRatio is { } raw ? Math.Clamp(raw, 0, 1) : 0;

    /// <summary>本月 80% 到未滿 100%（進度條黃色）。</summary>
    public bool IsMonthWarning => MonthRawRatio is { } raw && raw >= 0.8 && raw < 1;

    /// <summary>本月 100% 以上（進度條紅色）。</summary>
    public bool IsMonthDanger => MonthRawRatio is { } raw && raw >= 1;

    /// <summary>未截斷的比例（≥ 1 才是超過預算）；沒有預算回 null。</summary>
    private double? MonthRawRatio => _budgetUsd is { } budget ? (double)(_monthUsd / budget) : null;

    /// <summary>「用量與費用 ›」：開設定視窗到用量頁（沒有委派時什麼都不做）。</summary>
    [RelayCommand]
    private void OpenUsagePage() => _openUsagePage?.Invoke();

    // ---- 圓環的 Tooltip 與無障礙名稱 ----

    /// <summary>「上下文 9% · 點一下看詳情」；沒有資料是「尚無用量資料」；沒有上限時是面板標題（只有數字，沒有百分比）。</summary>
    public string RingTooltip
    {
        get
        {
            if (!HasData)
                return Strings.Usage_RingTipEmpty;
            return HasContextLimit
                ? string.Format(CultureInfo.CurrentCulture, Strings.Usage_RingTipFormat, PercentText)
                : Strings.Usage_Title;
        }
    }

    /// <summary>「本對話用量：上下文 9%」；沒有資料或沒有上限時的說法同 <see cref="RingTooltip"/>。</summary>
    public string RingAutomationName
    {
        get
        {
            if (!HasData)
                return Strings.Usage_RingNameEmpty;
            return HasContextLimit
                ? string.Format(CultureInfo.CurrentCulture, Strings.Usage_RingNameFormat, PercentText)
                : Strings.Usage_Title;
        }
    }
}
