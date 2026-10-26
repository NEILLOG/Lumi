using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;

namespace Lumi.App.ViewModels;

/// <summary>
/// 設定頁「資料與隱私」（M20 20C）：列出 Lumi 存放資料的位置（可開啟、可複製路徑、顯示大小）、
/// 說明哪些內容會送到模型，以及「怎麼控制」的連結（請求前往對應的設定分類）。唯讀，沒有任何刪除動作。
/// </summary>
public sealed partial class DataPrivacyViewModel : ObservableObject
{
    private readonly IFolderOpener _folders;
    private readonly IClipboardService _clipboard;

    public DataPrivacyViewModel(IDataLocationProvider locations, IFolderOpener folders, IClipboardService clipboard)
    {
        _folders = folders;
        _clipboard = clipboard;
        Locations = locations.GetLocations()
            .Select(location => new DataLocationItemViewModel(location, OpenLocationAsync, CopyLocationAsync))
            .ToList();
    }

    /// <summary>「怎麼控制」的連結按下時要求前往某個設定分類；SettingsViewModel 訂閱。</summary>
    public event Action<SettingsSection>? NavigateRequested;

    /// <summary>存放位置，順序就是畫面順序。</summary>
    public IReadOnlyList<DataLocationItemViewModel> Locations { get; }

    /// <summary>「開啟」按鈕的文字：macOS 是「在 Finder 中顯示」，其他是「在檔案總管中開啟」。</summary>
    public string OpenButtonText => OperatingSystem.IsMacOS() ? Strings.Privacy_OpenMac : Strings.Privacy_OpenWindows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    public bool HasMessage => Message is not null;

    /// <summary>重新計算每個位置的大小與存在狀態（進入頁面時呼叫）。大小在背景執行緒算。</summary>
    public async Task RefreshSizesAsync()
    {
        foreach (var item in Locations)
        {
            var path = item.Path;
            if (path is null)
                continue;

            if (!Exists(path, item.IsFile))
            {
                item.NotCreated = true;
                item.SizeText = null;
                continue;
            }

            item.NotCreated = false;
            var bytes = await Task.Run(() => ComputeSize(path, item.IsFile));
            item.SizeText = string.Format(CultureInfo.CurrentCulture, Strings.Privacy_SizeFormat, FormatSize(bytes));
        }
    }

    /// <summary>位元組數轉成人看得懂的文字：B、KB、MB、GB（小數一位，去掉結尾的 0）。</summary>
    public static string FormatSize(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes < kb)
            return $"{bytes} B";
        if (bytes < mb)
            return $"{(bytes / kb).ToString("0.#", CultureInfo.InvariantCulture)} KB";
        if (bytes < gb)
            return $"{(bytes / mb).ToString("0.#", CultureInfo.InvariantCulture)} MB";
        return $"{(bytes / gb).ToString("0.#", CultureInfo.InvariantCulture)} GB";
    }

    private async Task OpenLocationAsync(DataLocationItemViewModel item)
    {
        if (item.Path is not { } path)
            return;

        // 檔案（資料庫、設定檔）開啟它所在的資料夾；資料夾開啟自己
        var target = item.IsFile ? Path.GetDirectoryName(path)! : path;
        var opened = await _folders.OpenAsync(target);
        if (opened)
        {
            Message = null;
            MessageIsError = false;
        }
        else
        {
            Message = Strings.Privacy_OpenFailed;
            MessageIsError = true;
        }
    }

    private async Task CopyLocationAsync(DataLocationItemViewModel item)
    {
        if (item.Path is not { } path)
            return;

        await _clipboard.SetTextAsync(path);
        Message = Strings.Privacy_Copied;
        MessageIsError = false;
    }

    private static bool Exists(string path, bool isFile) => isFile ? File.Exists(path) : Directory.Exists(path);

    private static long ComputeSize(string path, bool isFile)
    {
        if (isFile)
            return new FileInfo(path).Length;

        return new DirectoryInfo(path)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Sum(file => file.Length);
    }

    /// <summary>「怎麼控制」的連結按下：參數是 <see cref="SettingsSection"/> 的名稱（例如 "Memory"）。</summary>
    [RelayCommand]
    private void GoTo(string section)
    {
        if (Enum.TryParse<SettingsSection>(section, out var target))
            NavigateRequested?.Invoke(target);
    }
}

/// <summary>「資料與隱私」的一列：一個存放位置。Path 是 null（鑰匙圈）時沒有路徑，開啟與複製都不能按。</summary>
public sealed partial class DataLocationItemViewModel : ObservableObject
{
    public DataLocationItemViewModel(DataLocation location, Func<DataLocationItemViewModel, Task> open, Func<DataLocationItemViewModel, Task> copy)
    {
        Id = location.Id;
        Name = location.Name;
        Description = location.Description;
        Path = location.Path;
        IsFile = location.IsFile;
        OpenCommand = new AsyncRelayCommand(() => open(this), () => HasPath);
        CopyCommand = new AsyncRelayCommand(() => copy(this), () => HasPath);
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    /// <summary>實際的完整路徑；沒有路徑時是 null。</summary>
    public string? Path { get; }

    /// <summary>Path 是檔案。</summary>
    public bool IsFile { get; }

    public string PathText => Path ?? "";

    public bool HasPath => Path is not null;

    [ObservableProperty]
    private string? _sizeText;

    /// <summary>有路徑但檔案或資料夾還不存在（例如還沒有人寄過訊息就沒有資料庫）。</summary>
    [ObservableProperty]
    private bool _notCreated;

    public IRelayCommand OpenCommand { get; }

    public IRelayCommand CopyCommand { get; }
}
