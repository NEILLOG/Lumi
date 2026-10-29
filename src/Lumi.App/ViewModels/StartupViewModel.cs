using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Core.Platform;
using Lumi.Core.Updates;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定視窗「外觀與操作」裡的「啟動與更新」：開機後自動啟動、啟動時檢查新版、目前版本與「立即檢查」。
/// 勾選立即生效；存不起來時還原並顯示原因。開機自動啟動的狀態以作業系統為準（登錄檔、LaunchAgent）。
/// </summary>
public sealed partial class StartupViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly ILaunchAtLogin? _launch;
    private readonly IUpdateService? _updates;
    private readonly IUrlLauncher? _launcher;
    private bool _launchAtLogin;
    private bool _checkForUpdates;
    private UpdateInfo? _found;

    public StartupViewModel(IModelCatalog catalog, IUiDispatcher dispatcher, ILaunchAtLogin? launch = null, IUpdateService? updates = null, IUrlLauncher? launcher = null)
    {
        _catalog = catalog;
        _launch = launch;
        _updates = updates;
        _launcher = launcher;
        _checkForUpdates = catalog.Current.Behavior.CheckForUpdates;
        _launchAtLogin = LaunchSupported && ReadLaunchAtLogin();
        _found = updates?.Available;
        if (_found is not null)
            _status = FoundText(_found);
        catalog.Changed += (_, settings) => dispatcher.Post(() =>
        {
            if (settings.Behavior.CheckForUpdates == _checkForUpdates)
                return;
            _checkForUpdates = settings.Behavior.CheckForUpdates;
            OnPropertyChanged(nameof(CheckForUpdates));
        });
    }

    public bool LaunchSupported => _launch?.IsSupported == true;

    public bool LaunchUnsupported => !LaunchSupported;

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.Update_CurrentVersionFormat, AppVersion.Current);

    public bool CanCheck => _updates is not null;

    /// <summary>開機後自動啟動。</summary>
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (value == _launchAtLogin || !LaunchSupported)
                return;
            _launchAtLogin = value;
            OnPropertyChanged();
            ApplyLaunchAtLogin(value);
        }
    }

    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (value == _checkForUpdates)
                return;
            _checkForUpdates = value;
            OnPropertyChanged();
            _ = ApplyCheckForUpdatesAsync(value);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    /// <summary>「立即檢查」的結果（最新、有新版、失敗原因）；還沒檢查過是 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _status;

    public bool HasStatus => Status is not null;

    /// <summary>檢查到新版時才有：下載頁按鈕。</summary>
    public bool HasUpdate => _found is not null;

    private bool ReadLaunchAtLogin()
    {
        try
        {
            return _launch!.IsEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ApplyLaunchAtLogin(bool enabled)
    {
        Error = null;
        try
        {
            _launch!.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = Strings.Startup_LaunchFailed;
            _launchAtLogin = ReadLaunchAtLogin();
            OnPropertyChanged(nameof(LaunchAtLogin));
        }
    }

    private async Task ApplyCheckForUpdatesAsync(bool enabled)
    {
        Error = null;
        try
        {
            await _catalog.SetBehaviorAsync(_catalog.Current.Behavior with { CheckForUpdates = enabled });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
            _checkForUpdates = _catalog.Current.Behavior.CheckForUpdates;
            OnPropertyChanged(nameof(CheckForUpdates));
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckNow))]
    private async Task CheckNowAsync()
    {
        IsChecking = true;
        Status = Strings.Update_Checking;
        try
        {
            var result = await _updates!.CheckAsync(manual: true);
            _found = result.Outcome == UpdateCheckOutcome.Available ? result.Update : null;
            Status = result.Outcome switch
            {
                UpdateCheckOutcome.Available => FoundText(result.Update!),
                UpdateCheckOutcome.Failed => string.Format(CultureInfo.CurrentCulture, Strings.Update_FailedFormat, result.Message),
                _ => Strings.Update_UpToDate,
            };
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(HasUpdate));
        }
    }

    private bool CanCheckNow() => _updates is not null && !IsChecking;

    [RelayCommand]
    private async Task OpenDownloadAsync()
    {
        if (_found is null || _launcher is null)
            return;
        if (!await _launcher.OpenAsync(_found.PageUrl))
            Status = string.Format(CultureInfo.CurrentCulture, Strings.Update_OpenFailedFormat, _found.PageUrl);
    }

    private static string FoundText(UpdateInfo update) => string.Format(CultureInfo.CurrentCulture, Strings.Update_AvailableFormat, update.Version);
}
