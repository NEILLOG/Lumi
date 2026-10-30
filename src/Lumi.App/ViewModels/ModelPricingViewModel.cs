using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Core.Usage;

namespace Lumi.App.ViewModels;

/// <summary>
/// 模型設定表單的「單價」段落（plan/23-usage-cost.md 第 6 節）。依 model ID 與連線比對價目表：
/// 完全相符或正規化後相符自動帶入（<see cref="PriceSource.Suggested"/>）；近似或前綴相符只列候選。
/// 「不計費」是使用者自己勾選的（例如 Gateway 後面的地端模型）：從網址分不出來，所以不自動判斷。
/// 使用者改過或確認過的單價是 <see cref="PriceSource.Confirmed"/>，連線改變時不覆蓋，只有 model ID 改了才重新比對。
/// </summary>
public partial class ModelPricingViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly IPriceTableSource _prices;
    private string _modelId = "";
    private ConnectionSettings? _connection;
    private PriceMatch? _match;
    private PriceSource _source;
    private bool _applying;

    public ModelPricingViewModel(IPriceTableSource prices)
    {
        _prices = prices;
    }

    /// <summary>比對到只有候選（近似或前綴相符）時的清單，最多 3 筆。</summary>
    public ObservableCollection<PriceCandidateItem> Candidates { get; } = [];

    /// <summary>不計費（例如地端模型）：用量照記、費用算 0；不比對價目表、不顯示單價欄位。使用者自己勾選。</summary>
    [ObservableProperty] private bool _isFree;

    // ---- 欄位（文字；空白＝未設定）----

    [ObservableProperty] private string _inputText = "";

    [ObservableProperty] private string _outputText = "";

    [ObservableProperty] private string _cacheReadText = "";

    [ObservableProperty] private string _cacheWriteText = "";

    /// <summary>使用者按了「編輯」或「自己填」。</summary>
    [ObservableProperty] private bool _isEditing;

    /// <summary>「進階：快取價格」是否展開（儲存的快取價或快取欄位的錯誤會自動展開）。</summary>
    [ObservableProperty] private bool _showAdvanced;

    [ObservableProperty] private bool _isUpdating;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdateStatus))] private string? _updateStatusText;

    public bool HasUpdateStatus => UpdateStatusText is not null;

    partial void OnIsFreeChanged(bool value)
    {
        if (_applying)
            return;
        _source = PriceSource.Confirmed;
        if (!value)
        {
            // 取消勾選：回到沒有單價的狀態，依比對結果重新帶入（完全相符會自動填、近似列候選）
            _source = PriceSource.None;
            IsEditing = false;
            MatchPrice(modelChanged: true, loading: false);
        }
        NotifyState();
    }

    partial void OnInputTextChanged(string value) => OnFieldEdited();

    partial void OnOutputTextChanged(string value) => OnFieldEdited();

    partial void OnCacheReadTextChanged(string value) => OnFieldEdited();

    partial void OnCacheWriteTextChanged(string value) => OnFieldEdited();

    partial void OnIsEditingChanged(bool value) => NotifyState();

    partial void OnIsUpdatingChanged(bool value)
    {
        UpdateTableCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(UpdateTableLabel));
    }

    private void OnFieldEdited()
    {
        if (_applying)
            return;
        _source = PriceSource.Confirmed;
        if (CacheReadError is not null || CacheWriteError is not null)
            ShowAdvanced = true;
        NotifyState();
    }

    // ---- 畫面狀態（唯讀）----


    /// <summary>目前的單價是自動帶入、還沒有經過使用者確認。</summary>
    public bool IsAutoFilled => _source == PriceSource.Suggested && !IsFree;

    public bool HasPrice => InputValue is not null && OutputValue is not null;

    /// <summary>只有候選、還沒有單價、使用者也還沒選「自己填」：列出候選，欄位先不顯示。</summary>
    public bool ShowCandidates => !IsFree && !IsEditing && !HasPrice && _match?.Kind == PriceMatchKind.Candidates;

    /// <summary>輸入框：編輯中，或還沒有單價（找不到的情況直接給空欄位）。</summary>
    public bool ShowFields => !IsFree && !ShowCandidates && (IsEditing || !HasPrice);

    /// <summary>一行摘要：有單價、沒在編輯。</summary>
    public bool ShowSummary => !IsFree && !ShowFields && !ShowCandidates;

    public bool ShowNotFound => ShowFields && _match?.Kind == PriceMatchKind.NotFound;

    public bool CanRestore => !IsFree && _match?.Kind is PriceMatchKind.Exact or PriceMatchKind.Normalized;

    public string CandidatesTitle => string.Format(CultureInfo.CurrentCulture, Strings.Pricing_CandidatesFormat, _modelId);

    public string SummaryText => string.Format(CultureInfo.CurrentCulture, Strings.Pricing_SummaryFormat,
        FormatPrice(InputValue ?? 0), FormatPrice(OutputValue ?? 0));

    public string TableDateText => string.Format(CultureInfo.CurrentCulture, Strings.Pricing_TableDateFormat,
        _prices.Current.SnapshotDate.ToString("yyyy-MM-dd", Inv));

    public string UpdateTableLabel => IsUpdating ? Strings.Pricing_Updating : Strings.Pricing_UpdateTable;

    public bool HasSelectedCandidate => Candidates.Any(c => c.IsSelected);

    // ---- 驗證：不是 0 以上的數字時顯示錯誤（空白是合法的＝未設定）----

    public string? InputError => ErrorFor(InputText);

    public string? OutputError => ErrorFor(OutputText);

    public string? CacheReadError => ErrorFor(CacheReadText);

    public string? CacheWriteError => ErrorFor(CacheWriteText);

    public bool HasError => InputError is not null || OutputError is not null || CacheReadError is not null || CacheWriteError is not null;

    private string? ErrorFor(string text) =>
        !IsFree && !string.IsNullOrWhiteSpace(text) && ParseValue(text) is null ? Strings.Pricing_Invalid : null;

    private decimal? InputValue => ParseValue(InputText);

    private decimal? OutputValue => ParseValue(OutputText);

    // ---- 對外 API ----

    /// <summary>
    /// 開啟編輯器或存檔後載入儲存的單價。已確認的單價保留；沒有單價（或是自動帶入的）依比對結果帶入。
    /// </summary>
    public void Load(ModelPricing stored, string modelId, ConnectionSettings? connection)
    {
        _modelId = modelId.Trim();
        _connection = connection;
        _source = stored.IsFree ? PriceSource.Confirmed : stored.Source;
        IsEditing = false;
        SetFree(stored.IsFree);
        SetTexts(stored.InputPerMTok, stored.OutputPerMTok, stored.CacheReadPerMTok, stored.CacheWritePerMTok);
        ShowAdvanced = stored.CacheReadPerMTok is not null || stored.CacheWritePerMTok is not null;
        MatchPrice(modelChanged: false, loading: true);
        NotifyState();
    }

    /// <summary>
    /// 模型 ID 或連線改變時重新比對。回傳本次新比對到（完全相符或正規化相符）的價目條目；只有 model ID 改變時才會有值，
    /// 新增模型用它帶入上下文上限與最大輸出。
    /// </summary>
    public PriceEntry? Rematch(string modelId, ConnectionSettings? connection)
    {
        var id = modelId.Trim();
        var modelChanged = !string.Equals(id, _modelId, StringComparison.Ordinal);
        _modelId = id;
        _connection = connection;
        IsEditing = false;
        var entry = MatchPrice(modelChanged, loading: false);
        NotifyState();
        return entry;
    }

    /// <summary>目前表單上的單價（存檔用）。本機一律免費；欄位都空白時來源是 <see cref="PriceSource.None"/>。</summary>
    public ModelPricing ToPricing()
    {
        if (IsFree)
            return new ModelPricing(null, null, IsFree: true, Source: PriceSource.Confirmed);

        var source = InputValue is null && OutputValue is null ? PriceSource.None : _source;
        return new ModelPricing(InputValue, OutputValue, ParseValue(CacheReadText), ParseValue(CacheWriteText), IsFree: false, Source: source);
    }

    /// <summary>單價顯示：至少兩位小數，最多四位（<c>3.00</c>、<c>0.075</c>、<c>0.0001</c>）。</summary>
    public static string FormatPrice(decimal value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.00##", Inv);

    // ---- 比對與套用 ----

    /// <returns>本次新比對到的條目（只在 model ID 改變且完全相符或正規化相符時）；其他情況是 null。</returns>
    private PriceEntry? MatchPrice(bool modelChanged, bool loading)
    {
        _match = _connection is null ? null : PriceMatcher.Match(_prices.Current, _modelId, _connection);
        RebuildCandidates();
        if (_match is null)
            return null;

        // 標了「不計費」就不比對、不動單價欄位
        if (IsFree)
            return null;

        // 使用者確認過的單價不因連線改變而覆蓋；model ID 改了才重新比對
        if (!modelChanged && _source == PriceSource.Confirmed)
            return null;

        switch (_match.Kind)
        {
            case PriceMatchKind.Exact:
            case PriceMatchKind.Normalized:
                ApplyEntry(_match.Entry!, PriceSource.Suggested);
                return modelChanged ? _match.Entry : null;
            default:
                // 只有候選或找不到：舊模型的單價不沿用；載入時保留儲存的值，讓使用者看得到
                if (!loading)
                {
                    SetTexts(null, null, null, null);
                    _source = PriceSource.None;
                }
                return null;
        }
    }

    private void RebuildCandidates()
    {
        Candidates.Clear();
        if (_match?.Kind != PriceMatchKind.Candidates)
            return;
        foreach (var entry in _match.Candidates)
            Candidates.Add(new PriceCandidateItem(entry, OnCandidateSelected));
    }

    private void OnCandidateSelected(PriceCandidateItem selected)
    {
        foreach (var item in Candidates)
        {
            if (!ReferenceEquals(item, selected))
                item.IsSelected = false;
        }
        ApplyCandidateCommand.NotifyCanExecuteChanged();
    }

    private void ApplyEntry(PriceEntry entry, PriceSource source)
    {
        SetTexts(entry.InputPerMTok, entry.OutputPerMTok, entry.CacheReadPerMTok, entry.CacheWritePerMTok);
        _source = source;
    }

    /// <summary>程式設定欄位文字（不算使用者編輯，不會把來源改成已確認）。</summary>
    private void SetTexts(decimal? input, decimal? output, decimal? cacheRead, decimal? cacheWrite)
    {
        _applying = true;
        try
        {
            InputText = ToText(input);
            OutputText = ToText(output);
            CacheReadText = ToText(cacheRead);
            CacheWriteText = ToText(cacheWrite);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>程式設定「不計費」（不算使用者編輯）。</summary>
    private void SetFree(bool free)
    {
        _applying = true;
        try
        {
            IsFree = free;
        }
        finally
        {
            _applying = false;
        }
    }

    private static string ToText(decimal? value) => value is { } v ? FormatPrice(v) : "";

    private static decimal? ParseValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
            return null;
        return value;
    }

    // ---- 命令 ----

    [RelayCommand]
    private void Edit() => IsEditing = true;

    /// <summary>候選清單裡選好的那一筆：套用後是已確認的單價。</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedCandidate))]
    private void ApplyCandidate()
    {
        var selected = Candidates.FirstOrDefault(c => c.IsSelected);
        if (selected is null)
            return;
        ApplyEntry(selected.Entry, PriceSource.Confirmed);
        IsEditing = false;
        NotifyState();
    }

    /// <summary>「自己填」：候選清單收起，顯示空的欄位。</summary>
    [RelayCommand]
    private void Manual() => IsEditing = true;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void Restore()
    {
        if (_match is not { Entry: { } entry })
            return;
        ApplyEntry(entry, PriceSource.Suggested);
        IsEditing = false;
        NotifyState();
    }

    public bool CanUpdateTable => !IsUpdating;

    /// <summary>從網路更新價目表。成功後重新比對（已確認的單價保留）；失敗沿用目前的價目表。</summary>
    [RelayCommand(CanExecute = nameof(CanUpdateTable))]
    private async Task UpdateTableAsync(CancellationToken ct)
    {
        IsUpdating = true;
        UpdateStatusText = Strings.Pricing_Updating;
        try
        {
            var result = await _prices.UpdateAsync(ct);
            if (result.Success)
            {
                UpdateStatusText = string.Format(CultureInfo.CurrentCulture, Strings.Pricing_UpdatedFormat,
                    result.SnapshotDate.ToString("yyyy-MM-dd", Inv));
                MatchPrice(modelChanged: false, loading: false);
            }
            else
            {
                UpdateStatusText = Strings.Pricing_UpdateFailed;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 更新過程出錯（網路、寫檔等）一律當成失敗，沿用目前的價目表
            UpdateStatusText = Strings.Pricing_UpdateFailed;
        }
        finally
        {
            IsUpdating = false;
            NotifyState();
        }
    }

    // ---- 通知 ----

    /// <summary>計算屬性會依多個欄位改變，這裡一次通知（命令的可執行狀態也一起更新）。</summary>
    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsFree));
        OnPropertyChanged(nameof(IsAutoFilled));
        OnPropertyChanged(nameof(HasPrice));
        OnPropertyChanged(nameof(ShowCandidates));
        OnPropertyChanged(nameof(ShowFields));
        OnPropertyChanged(nameof(ShowSummary));
        OnPropertyChanged(nameof(ShowNotFound));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CandidatesTitle));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TableDateText));
        OnPropertyChanged(nameof(HasSelectedCandidate));
        OnPropertyChanged(nameof(InputError));
        OnPropertyChanged(nameof(OutputError));
        OnPropertyChanged(nameof(CacheReadError));
        OnPropertyChanged(nameof(CacheWriteError));
        OnPropertyChanged(nameof(HasError));
        ApplyCandidateCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>候選清單的一列（近似或前綴相符的價目條目）。同一時間只能選一筆，由 <see cref="ModelPricingViewModel"/> 負責。</summary>
public sealed class PriceCandidateItem : ObservableObject
{
    private readonly Action<PriceCandidateItem> _onSelected;
    private bool _isSelected;

    public PriceCandidateItem(PriceEntry entry, Action<PriceCandidateItem> onSelected)
    {
        Entry = entry;
        _onSelected = onSelected;
    }

    public PriceEntry Entry { get; }

    /// <summary>價目表裡的鍵名，例如 <c>azure/gpt-4o</c>。</summary>
    public string ModelId => Entry.Key;

    public string PriceText => string.Format(CultureInfo.CurrentCulture, Strings.Pricing_CandidatePriceFormat,
        ModelPricingViewModel.FormatPrice(Entry.InputPerMTok), ModelPricingViewModel.FormatPrice(Entry.OutputPerMTok));

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value) && value)
                _onSelected(this);
        }
    }
}
