using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Context;

namespace Lumi.App.ViewModels;

/// <summary>輸入框上方的一個 chip：使用者加入這則對話的檔案、資料夾或圖片，加上目前的狀態（ui-spec 6.1）。</summary>
public partial class ContextChipViewModel : ObservableObject
{
    public ContextChipViewModel(ContextItem item, ContextItemStatus status)
    {
        Id = item.Id;
        Kind = item.Kind;
        Path = item.Path;
        DisplayName = item.DisplayName;
        _isSent = !item.IsPending;
        _status = status;
    }

    public string Id { get; }

    public ContextKind Kind { get; }

    public string Path { get; }

    public string DisplayName { get; }

    public bool IsFile => Kind == ContextKind.File;

    public bool IsFolder => Kind == ContextKind.Folder;

    public bool IsImage => Kind == ContextKind.Image;

    /// <summary>已經帶進過一則訊息（仍在範圍內）；false＝下一則訊息會帶上它目前的內容。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SentLabel))]
    private bool _isSent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail), nameof(HasDetail), nameof(HasProblem), nameof(Tooltip))]
    private ContextItemStatus _status;

    public bool HasProblem => Status.HasProblem;

    public string? SentLabel => IsSent ? Strings.Context_Sent : null;

    /// <summary>名稱後面的一小段說明：資料夾的項目數、檔案大小，或有問題時的原因。</summary>
    public string? Detail => Status.Problem switch
    {
        ContextItemProblem.Missing => Strings.Context_Missing,
        ContextItemProblem.NotText => Strings.Context_NotText,
        ContextItemProblem.TooLarge => Strings.Context_TooLarge,
        ContextItemProblem.DocumentByTool => Strings.Context_DocumentByTool,
        ContextItemProblem.UnsupportedImage => Strings.Context_ImageUnsupported,
        ContextItemProblem.ImageTooLarge => Strings.Context_ImageTooLarge,
        _ when Kind == ContextKind.Folder && Status.EntryCount is { } count => string.Format(
            CultureInfo.CurrentCulture,
            Status.EntryCountCapped ? Strings.Context_FolderEntriesCapped : Strings.Context_FolderEntries,
            count.ToString("N0", CultureInfo.CurrentCulture)),
        _ when Status.SizeBytes is { } size => FormatSize(size),
        _ => null,
    };

    public bool HasDetail => Detail is not null;

    public string Tooltip => Detail is null ? Path : $"{Path}\n{Detail}";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0 / 1024.0:0.#} MB"),
    };
}
