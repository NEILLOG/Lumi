using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Browser;
using Lumi.Core.Models;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定視窗「網路」底部的「瀏覽器」區段（M18 D1、D2、D3）：使用的瀏覽器、顯示瀏覽器視窗、清除登入狀態。
/// 三項都立即生效（寫進設定，不需要「儲存」）；視窗與瀏覽器的選擇在下次開啟瀏覽器時才套用。
/// 偵測結果在建構時做一次，之後在切換瀏覽器與「重新偵測」時重做。
/// </summary>
public partial class BrowserSettingsViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly IBrowserLocator _locator;
    private readonly IBrowserSessions _sessions;
    private readonly IConfirmService _confirm;
    private int _choiceIndex;
    private bool _showWindow;
    private bool _loading;
    private BrowserAvailability _availability;

    public BrowserSettingsViewModel(IModelCatalog catalog, IBrowserLocator locator, IBrowserSessions sessions, IConfirmService confirm,
        IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        _locator = locator;
        _sessions = sessions;
        _confirm = confirm;

        var saved = catalog.Current.Browser;
        _choiceIndex = (int)saved.Choice;
        _showWindow = saved.ShowWindow;
        _availability = locator.Resolve(saved.Choice);

        RefreshCommand = new RelayCommand(() => SetAvailability(_locator.Resolve(CurrentChoice)));
        catalog.Changed += (_, settings) => dispatcher.Post(() => Reload(settings.Browser));
    }

    /// <summary>0 自動（Edge 優先，其次 Chrome）、1 Microsoft Edge、2 Google Chrome（對應 <see cref="BrowserChoice"/>）。</summary>
    public int ChoiceIndex
    {
        get => _choiceIndex;
        set
        {
            if (value == _choiceIndex)
                return;
            _choiceIndex = value;
            OnPropertyChanged();
            if (_loading)
                return;
            SetAvailability(_locator.Resolve(CurrentChoice));
            _ = ApplyAsync(s => s with { Choice = (BrowserChoice)value });
        }
    }

    /// <summary>顯示瀏覽器視窗（預設關閉＝背景執行）。</summary>
    public bool ShowWindow
    {
        get => _showWindow;
        set
        {
            if (value == _showWindow)
                return;
            _showWindow = value;
            OnPropertyChanged();
            if (_loading)
                return;
            _ = ApplyAsync(s => s with { ShowWindow = value });
        }
    }

    private BrowserChoice CurrentChoice => (BrowserChoice)ChoiceIndex;

    /// <summary>偵測到的瀏覽器（「找到：…」一行）；找不到任何一個時是找不到的說明。選了沒裝的那個時，多一行原因。</summary>
    public string DetectionText
    {
        get
        {
            var lines = new List<string>();
            if (_availability.Installed.Count > 0)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Browser_Found,
                    string.Join("、", _availability.Installed.Select(Describe))));
            if (!_availability.IsAvailable)
                lines.Add(_availability.Installed.Count == 0 ? Strings.Browser_NotFound : _availability.Problem ?? Strings.Browser_NotFound);
            return string.Join("\n", lines);
        }
    }

    /// <summary>目前的選擇沒有可用的瀏覽器：偵測文字改用警告樣式。</summary>
    public bool HasProblem => !_availability.IsAvailable;

    /// <summary>「重新偵測」：瀏覽器可能剛裝或剛移除。</summary>
    public IRelayCommand RefreshCommand { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearProfileCommand))]
    private bool _isClearing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public bool HasMessage => Message is not null;

    /// <summary>清除瀏覽器的登入狀態（整個專屬設定檔）：先確認；執行中停用。</summary>
    [RelayCommand(CanExecute = nameof(CanClearProfile))]
    private async Task ClearProfileAsync()
    {
        var confirmed = await _confirm.ConfirmDestructiveAsync(Strings.Browser_ClearConfirm, Strings.Browser_ClearConfirmButton);
        if (!confirmed)
            return;

        IsClearing = true;
        try
        {
            await _sessions.ClearProfileAsync(CancellationToken.None);
            SetMessage(Strings.Browser_Cleared, isError: false);
        }
        catch (BrowserException ex)
        {
            SetMessage(ex.Message, isError: true);
        }
        finally
        {
            IsClearing = false;
        }
    }

    private bool CanClearProfile() => !IsClearing;

    private static string Describe(InstalledBrowser browser) =>
        browser.Version is null ? browser.DisplayName : $"{browser.DisplayName} {browser.Version}";

    private void SetAvailability(BrowserAvailability availability)
    {
        _availability = availability;
        OnPropertyChanged(nameof(DetectionText));
        OnPropertyChanged(nameof(HasProblem));
    }

    private void SetMessage(string text, bool isError)
    {
        Message = text;
        MessageIsError = isError;
    }

    private async Task ApplyAsync(Func<BrowserSettings, BrowserSettings> change)
    {
        Message = null;
        try
        {
            await _catalog.SetBrowserAsync(change(_catalog.Current.Browser));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            SetMessage(ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed, isError: true);
            Reload(_catalog.Current.Browser);   // 存不起來：畫面回到實際生效的值
        }
    }

    /// <summary>設定在別處變了（或剛才儲存失敗）：畫面跟著實際的值；只有瀏覽器的選擇改變時才重新偵測。</summary>
    private void Reload(BrowserSettings saved)
    {
        var previousChoice = _choiceIndex;
        _loading = true;
        try
        {
            ChoiceIndex = (int)saved.Choice;
            ShowWindow = saved.ShowWindow;
        }
        finally
        {
            _loading = false;
        }

        if (_choiceIndex != previousChoice)
            SetAvailability(_locator.Resolve(CurrentChoice));
    }
}
