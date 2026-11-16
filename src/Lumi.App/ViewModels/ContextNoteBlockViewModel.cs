using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.ContextNotes;

namespace Lumi.App.ViewModels;

/// <summary>區塊內的一段：文字（Markdown），或單獨一行的圖片（<c>![…](images/…)</c>）。</summary>
/// <param name="ImagePath">圖片的完整路徑；找不到時是 null。</param>
/// <param name="ImageMissing">圖片行存在但檔案找不到（顯示「找不到圖片」）。</param>
public sealed record ContextNotePart(string Markdown, string? ImagePath, bool ImageMissing)
{
    public bool HasImage => ImagePath is not null;

    public bool IsImage => ImagePath is not null || ImageMissing;

    public bool IsText => !IsImage;
}

/// <summary>研究本的一個區塊（一列）：主文、原話、剛更新／受保護的標記，以及「這一段的操作」。</summary>
public sealed partial class ContextNoteBlockViewModel : ObservableObject
{
    private static readonly Regex ImageLine = new(@"^!\[[^\]]*\]\((images/[^)\s]+)\)$", RegexOptions.CultureInvariant);

    private readonly IContextNoteService _notes;
    private readonly string _noteId;

    public ContextNoteBlockViewModel(string noteId, string section, string text, bool isUpdated, bool isProtected,
        IContextNoteService notes, IContextNoteFiles files)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(files);
        _noteId = noteId;
        _notes = notes;
        Section = section;
        Text = text;
        (MainText, Quote) = ContextNoteQuote.Split(text);
        IsUpdated = isUpdated;
        IsProtected = isProtected;
        Parts = BuildParts(noteId, MainText, files);
    }

    public string Section { get; }

    /// <summary>區塊的完整文字（含原話）；保護與標成已決定都用它比對。</summary>
    public string Text { get; }

    /// <summary>主文（去掉原話）。</summary>
    public string MainText { get; }

    /// <summary>原話；沒有就是 null。</summary>
    public string? Quote { get; }

    public bool HasQuote => Quote is not null;

    /// <summary>原話展開（預設收起）。</summary>
    [ObservableProperty]
    private bool _isQuoteOpen;

    /// <summary>這一版相對上一版新增或改過的區塊（只在最新一版是書記產生時）。</summary>
    public bool IsUpdated { get; }

    /// <summary>使用者保護的區塊（「你寫的」）。</summary>
    public bool IsProtected { get; }

    /// <summary>主文切成文字與圖片的段落（圖片行單獨成一段）。</summary>
    public IReadOnlyList<ContextNotePart> Parts { get; }

    public string QuoteToggleText => IsQuoteOpen ? Strings.ContextNote_Quote_Hide : Strings.ContextNote_Quote_Show;

    public bool CanMarkDecided => Section != ContextNoteDocument.Decided;

    public bool CanConfirm => Section == ContextNoteDocument.Decided && !IsProtected;

    public bool CanUnprotect => IsProtected;

    partial void OnIsQuoteOpenChanged(bool value) => OnPropertyChanged(nameof(QuoteToggleText));

    [RelayCommand]
    private Task MarkDecidedAsync() => _notes.MarkDecidedAsync(_noteId, Text);

    [RelayCommand]
    private Task ConfirmAsync() => _notes.SetProtectedAsync(_noteId, Text, true);

    [RelayCommand]
    private Task UnprotectAsync() => _notes.SetProtectedAsync(_noteId, Text, false);

    private static IReadOnlyList<ContextNotePart> BuildParts(string noteId, string main, IContextNoteFiles files)
    {
        var parts = new List<ContextNotePart>();
        var lines = new List<string>();
        foreach (var line in main.Split('\n'))
        {
            var match = ImageLine.Match(line.Trim());
            if (!match.Success)
            {
                lines.Add(line.TrimEnd());
                continue;
            }
            AddText(parts, lines);
            var path = files.ResolveImagePath(noteId, match.Groups[1].Value);
            parts.Add(new ContextNotePart("", path, path is null));
        }
        AddText(parts, lines);
        if (parts.Count == 0)
            parts.Add(new ContextNotePart(main, null, false));
        return parts;
    }

    private static void AddText(List<ContextNotePart> parts, List<string> lines)
    {
        var markdown = string.Join('\n', lines);
        lines.Clear();
        if (markdown.Trim().Length > 0)
            parts.Add(new ContextNotePart(markdown, null, false));
    }
}

/// <summary>研究本的一個區段（七個之一）。旁支預設收合，標題旁顯示則數。</summary>
public sealed partial class ContextNoteSectionViewModel : ObservableObject
{
    public ContextNoteSectionViewModel(string name, IReadOnlyList<ContextNoteBlockViewModel> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        Name = name;
        Blocks = blocks;
        IsCollapsed = IsSide;
    }

    public string Name { get; }

    public IReadOnlyList<ContextNoteBlockViewModel> Blocks { get; }

    public bool IsEmpty => Blocks.Count == 0;

    public bool IsSide => Name == ContextNoteDocument.Side;

    [ObservableProperty]
    private bool _isCollapsed;

    public bool ShowBlocks => !IsCollapsed;

    public bool ShowEmpty => IsEmpty && !IsCollapsed;

    public string CountText => string.Format(CultureInfo.CurrentCulture, Strings.ContextNote_Side_CountFormat, Blocks.Count);

    partial void OnIsCollapsedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBlocks));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    [RelayCommand]
    private void ToggleCollapsed() => IsCollapsed = !IsCollapsed;
}
