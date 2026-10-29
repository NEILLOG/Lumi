using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Updates;

namespace Lumi.App.ViewModels;

/// <summary>
/// 工作台頂端的「有新版」提醒：啟動時檢查到新版（沒被略過）就出現，可以看更新內容、前往下載頁、略過此版本，
/// 或先關掉（下次啟動檢查到仍會提醒）。沒有新版時整列不佔空間。
/// </summary>
public sealed partial class UpdateNoticeViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly IUrlLauncher _launcher;
    private readonly IUiDispatcher _dispatcher;
    private UpdateInfo? _update;

    public UpdateNoticeViewModel(IUpdateService updates, IUrlLauncher launcher, IUiDispatcher dispatcher)
    {
        _updates = updates;
        _launcher = launcher;
        _dispatcher = dispatcher;
        _updates.AvailableChanged += (_, _) => _dispatcher.Post(Refresh);
        Refresh();
    }

    /// <summary>空的提醒（沒有更新服務時用，永遠不顯示）。</summary>
    public static UpdateNoticeViewModel Inert { get; } = new(new InertUpdateService(), new InertUrlLauncher(), new InertDispatcher());

    public bool IsVisible => _update is not null;

    public string Title => _update is null ? "" : string.Format(CultureInfo.CurrentCulture, Strings.Update_BannerFormat, _update.Version);

    public string Subtitle => string.Format(CultureInfo.CurrentCulture, Strings.Update_BannerCurrentFormat, AppVersion.Current);

    /// <summary>發佈說明（Markdown）；沒有附說明時是「沒有附上更新說明」。</summary>
    public string Notes => _update is null ? "" : _update.Notes.Length > 0 ? _update.Notes : Strings.Update_NoNotes;

    [ObservableProperty]
    private bool _notesExpanded;

    public string NotesToggleText => NotesExpanded ? Strings.Update_HideNotes : Strings.Update_ShowNotes;

    /// <summary>開啟下載頁失敗時的說明（含網址，讓使用者自己複製開啟）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    public bool HasMessage => Message is not null;

    partial void OnNotesExpandedChanged(bool value) => OnPropertyChanged(nameof(NotesToggleText));

    [RelayCommand]
    private void ToggleNotes() => NotesExpanded = !NotesExpanded;

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_update is null)
            return;
        Message = null;
        if (!await _launcher.OpenAsync(_update.PageUrl))
            Message = string.Format(CultureInfo.CurrentCulture, Strings.Update_OpenFailedFormat, _update.PageUrl);
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (_update is null)
            return;
        try
        {
            await _updates.SkipAsync(_update);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Message = ex is InvalidOperationException ? ex.Message : Strings.Settings_SaveFailed;
        }
    }

    [RelayCommand]
    private void Dismiss() => _updates.Dismiss();

    private void Refresh()
    {
        _update = _updates.Available;
        NotesExpanded = false;
        Message = null;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Notes));
    }

    private sealed class InertUpdateService : IUpdateService
    {
        public UpdateInfo? Available => null;

        public event EventHandler? AvailableChanged { add { } remove { } }

        public Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken ct = default) =>
            Task.FromResult(new UpdateCheckResult(UpdateCheckOutcome.Disabled));

        public Task SkipAsync(UpdateInfo update, CancellationToken ct = default) => Task.CompletedTask;

        public void Dismiss()
        {
        }
    }

    private sealed class InertUrlLauncher : IUrlLauncher
    {
        public Task<bool> OpenAsync(Uri url) => Task.FromResult(false);
    }

    private sealed class InertDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }
}
