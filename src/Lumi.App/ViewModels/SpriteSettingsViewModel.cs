using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Sprites.Abstractions;
using Lumi.Sprites.Assets;

namespace Lumi.App.ViewModels;

/// <summary>角色清單裡的一項。</summary>
public sealed record SpriteOption(ISpriteFactory Factory)
{
    public string Id => Factory.Manifest.Id;

    public string Name => Factory.Manifest.Name;

    public string Credit => string.Format(CultureInfo.CurrentCulture, Strings.SpriteSettings_AuthorFormat, Factory.Manifest.Author, Factory.Manifest.Version);

    /// <summary>素材型角色（用圖片加 manifest.json 做的）；其他是程式型。</summary>
    public bool IsAsset => Factory is AssetSpriteFactory;

    public string KindText => IsAsset ? Strings.SpriteSettings_Kind_Asset : Strings.SpriteSettings_Kind_Code;

    /// <summary>清單的縮圖（素材型角色的 preview.png，載入時已經檢查過大小）；沒有就是 null。</summary>
    public string? PreviewPath => (Factory as AssetSpriteFactory)?.Definition.PreviewPath;
}

/// <summary>「載入失敗與警告」清單的一項。</summary>
public sealed record SpriteIssueItem(string Text, bool IsFatal)
{
    /// <summary>種類的文字標籤（失敗或警告）：不能只靠顏色區分。</summary>
    public string KindText => IsFatal ? Strings.SpriteSettings_Issue_Fatal : Strings.SpriteSettings_Issue_Warning;
}

public sealed record SpriteSizeOption(SpriteSize Size)
{
    public string Text => Size switch
    {
        SpriteSize.Small => Strings.SpriteSettings_Size_Small,
        SpriteSize.Large => Strings.SpriteSettings_Size_Large,
        _ => Strings.SpriteSettings_Size_Medium,
    };

    public static IReadOnlyList<SpriteSizeOption> All { get; } = Enum.GetValues<SpriteSize>().Select(s => new SpriteSizeOption(s)).ToList();
}

/// <summary>狀態預覽清單的一項：十個狀態各一個；角色沒有專屬表現的狀態標示它會退化成哪個。</summary>
public sealed record SpriteStateItem(SpriteState State, SpriteState Resolved)
{
    public string Label => StateName(State);

    public bool IsNative => State == Resolved;

    public string Note => IsNative
        ? Strings.SpriteSettings_PreviewNative
        : string.Format(CultureInfo.CurrentCulture, Strings.SpriteSettings_PreviewFallback, StateName(Resolved));

    public static string StateName(SpriteState state) => state switch
    {
        SpriteState.Listening => Strings.Sprite_State_Listening,
        SpriteState.Transcribing => Strings.Sprite_State_Transcribing,
        SpriteState.Thinking => Strings.Sprite_State_Thinking,
        SpriteState.Tool => Strings.Sprite_State_Tool,
        SpriteState.Streaming => Strings.Sprite_State_Streaming,
        SpriteState.Done => Strings.Sprite_State_Done,
        SpriteState.Attention => Strings.Sprite_State_Attention,
        SpriteState.Error => Strings.Sprite_State_Error,
        SpriteState.Sleep => Strings.Sprite_State_Sleep,
        _ => Strings.Sprite_State_Idle,
    };
}

/// <summary>
/// 設定視窗「小精靈」：角色清單、尺寸、角色語氣、狀態預覽。選了立即生效（寫進設定，小精靈視窗聽設定變更重建角色），
/// 不需要「儲存」。預覽區的角色由視窗的 code-behind 依 <see cref="PreviewFactory"/> 與 <see cref="PreviewState"/> 播放。
/// </summary>
public partial class SpriteSettingsViewModel : ObservableObject
{
    private readonly IModelCatalog _catalog;
    private readonly ISpriteCatalog _sprites;
    private readonly IReloadableSpriteCatalog? _library;
    private readonly IFolderOpener? _folders;
    private SpriteOption _selectedSprite;
    private SpriteSizeOption _selectedSize;
    private bool _voiceEnabled;

    public SpriteSettingsViewModel(IModelCatalog catalog, ISpriteCatalog sprites, IUiDispatcher dispatcher, IFolderOpener? folders = null)
    {
        _catalog = catalog;
        _sprites = sprites;
        _library = sprites as IReloadableSpriteCatalog;
        _folders = folders;
        _options = BuildList();
        if (_options.Count == 0)
            throw new InvalidOperationException("沒有任何已註冊的小精靈角色。");

        var saved = catalog.Current.Sprite;
        _selectedSprite = Find(saved.SpriteId);
        _selectedSize = SpriteSizeOption.All.First(o => o.Size == saved.Size);
        _voiceEnabled = saved.VoiceEnabled;
        RebuildStates();
        RefreshIssues();

        catalog.Changed += (_, settings) => dispatcher.Post(() => Reload(settings.Sprite));
        if (_library is not null)
            _library.Changed += (_, _) => dispatcher.Post(OnLibraryChanged);
    }

    private IReadOnlyList<SpriteOption> _options;

    public IReadOnlyList<SpriteOption> Sprites => _options;

    /// <summary>目錄可以重新載入（有「重新載入」「開啟角色資料夾」）。</summary>
    public bool CanReload => _library is not null;

    /// <summary>角色資料夾的位置；沒有設定時是 null。</summary>
    public string? SpritesDirectory => _library?.Directory;

    public bool HasSpritesDirectory => SpritesDirectory is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReloadLabel))]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool _isReloading;

    public string ReloadLabel => IsReloading ? Strings.SpriteSettings_Reloading : Strings.SpriteSettings_Reload;

    /// <summary>載入失敗與警告（目錄掃描的結果，加上素材型角色解碼時才發現的問題）。</summary>
    public ObservableCollection<SpriteIssueItem> Issues { get; } = [];

    public bool HasIssues => Issues.Count > 0;

    private IReadOnlyList<SpriteOption> BuildList() => [.. _sprites.All.Select(f => new SpriteOption(f))];

    public IReadOnlyList<SpriteSizeOption> Sizes => SpriteSizeOption.All;

    public ISpriteFactory PreviewFactory => _selectedSprite.Factory;

    [ObservableProperty]
    private IReadOnlyList<SpriteStateItem> _states = [];

    /// <summary>預覽區正在播放的狀態（使用者點的那個；角色不支援時視窗會依退化規則換成支援的）。</summary>
    [ObservableProperty]
    private SpriteStateItem? _previewState;

    [ObservableProperty]
    private string? _error;

    public SpriteOption SelectedSprite
    {
        get => _selectedSprite;
        set
        {
            if (value is null)
                return;
            // 設定裡存的角色不在清單裡時（資料夾被移走），畫面顯示的是第一個；使用者明確選它就要存起來
            var needsSave = value.Id != _catalog.Current.Sprite.SpriteId;
            if (value.Id == _selectedSprite.Id && !needsSave)
                return;
            _selectedSprite = value;
            OnSelectedSpriteChanged();
            if (needsSave)
                _ = ApplyAsync(s => s with { SpriteId = value.Id });
        }
    }

    public SpriteSizeOption SelectedSize
    {
        get => _selectedSize;
        set
        {
            if (value is null || value == _selectedSize)
                return;
            _selectedSize = value;
            OnPropertyChanged();
            _ = ApplyAsync(s => s with { Size = value.Size });
        }
    }

    public bool VoiceEnabled
    {
        get => _voiceEnabled;
        set
        {
            if (value == _voiceEnabled)
                return;
            _voiceEnabled = value;
            OnPropertyChanged();
            _ = ApplyAsync(s => s with { VoiceEnabled = value });
        }
    }

    /// <summary>目前角色有沒有語氣設定（光球沒有）：沒有的時候開關只是預先設定，說明文字會告訴使用者。</summary>
    public bool HasVoice => !string.IsNullOrWhiteSpace(_selectedSprite.Factory.Manifest.Voice);

    public bool HasNoVoice => !HasVoice;

    private SpriteOption Find(string id) => Sprites.FirstOrDefault(s => s.Id == id) ?? Sprites[0];

    [RelayCommand(CanExecute = nameof(CanExecuteReload))]
    private async Task ReloadAsync()
    {
        if (_library is null)
            return;
        IsReloading = true;
        try
        {
            await _library.ReloadAsync();   // 完成時 Changed 會重建清單
        }
        finally
        {
            IsReloading = false;
        }
    }

    private bool CanExecuteReload() => _library is not null && !IsReloading;

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Error = null;
        if (SpritesDirectory is not { } directory || _folders is null)
            return;
        if (!await _folders.OpenAsync(directory))
            Error = Strings.SpriteSettings_OpenFolderFailed;
    }

    /// <summary>
    /// 重新載入之後：清單整份換掉。選中的角色依設定裡存的 id 找；找不到（資料夾被移走）就顯示第一個，
    /// 但<b>不</b>改設定——角色回來之後重新載入就自動換回去。
    /// </summary>
    private void OnLibraryChanged()
    {
        var previous = _selectedSprite;
        _options = BuildList();
        OnPropertyChanged(nameof(Sprites));
        _selectedSprite = Find(_catalog.Current.Sprite.SpriteId);
        if (!ReferenceEquals(previous.Factory, _selectedSprite.Factory))
            OnSelectedSpriteChanged();   // 換了角色，或同一個角色重新載入了（圖片可能改過）：預覽重建
        else
            OnPropertyChanged(nameof(SelectedSprite));
        RefreshIssues();
    }

    /// <summary>目錄的載入結果，加上素材型角色解碼時才發現的問題（動畫第一次播放才會有）。預覽播放之後會再呼叫。</summary>
    public void RefreshIssues()
    {
        var items = new List<SpriteIssueItem>();
        if (_library is not null)
            items.AddRange(_library.Issues.Select(i => new SpriteIssueItem(i.ToString(), i.Fatal)));
        foreach (var option in Sprites)
        {
            if (option.Factory is AssetSpriteFactory asset)
                items.AddRange(asset.DecodeIssues.Select(i => new SpriteIssueItem(i.ToString(), i.Fatal)));
        }

        if (items.Select(i => i.Text).SequenceEqual(Issues.Select(i => i.Text)))
            return;
        Issues.Clear();
        foreach (var item in items)
            Issues.Add(item);
        OnPropertyChanged(nameof(HasIssues));
    }

    private void OnSelectedSpriteChanged()
    {
        OnPropertyChanged(nameof(SelectedSprite));
        OnPropertyChanged(nameof(PreviewFactory));
        OnPropertyChanged(nameof(HasVoice));
        OnPropertyChanged(nameof(HasNoVoice));
        RebuildStates();
    }

    /// <summary>十個狀態各一項，並標示這個角色會不會退化（<see cref="SpriteStateResolver"/> 的規則，和殼用的一樣）。</summary>
    private void RebuildStates()
    {
        var manifest = _selectedSprite.Factory.Manifest;
        var items = Enum.GetValues<SpriteState>().Select(s => new SpriteStateItem(s, SpriteStateResolver.Resolve(s, manifest))).ToList();
        States = items;
        PreviewState = items.FirstOrDefault(i => i.State == PreviewState?.State) ?? items[0];
    }

    private async Task ApplyAsync(Func<SpriteSettings, SpriteSettings> change)
    {
        Error = null;
        try
        {
            await _catalog.SetSpriteAsync(change(_catalog.Current.Sprite));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = ex is InvalidOperationException ? ex.Message : Strings.SpriteSettings_SaveFailed;
            Reload(_catalog.Current.Sprite);   // 存不起來：畫面回到實際生效的值
        }
    }

    /// <summary>設定在別處變了（或剛才儲存失敗）：畫面跟著實際的值。</summary>
    private void Reload(SpriteSettings saved)
    {
        var sprite = Find(saved.SpriteId);
        if (sprite.Id != _selectedSprite.Id)
        {
            _selectedSprite = sprite;
            OnSelectedSpriteChanged();
        }
        var size = SpriteSizeOption.All.First(o => o.Size == saved.Size);
        if (size != _selectedSize)
        {
            _selectedSize = size;
            OnPropertyChanged(nameof(SelectedSize));
        }
        if (saved.VoiceEnabled != _voiceEnabled)
        {
            _voiceEnabled = saved.VoiceEnabled;
            OnPropertyChanged(nameof(VoiceEnabled));
        }
    }
}
