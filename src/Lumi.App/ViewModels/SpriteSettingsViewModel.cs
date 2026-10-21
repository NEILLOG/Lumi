using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.App.Resources;
using Lumi.Core.Models;
using Lumi.Sprites.Abstractions;

namespace Lumi.App.ViewModels;

/// <summary>角色清單裡的一項。</summary>
public sealed record SpriteOption(ISpriteFactory Factory)
{
    public string Id => Factory.Manifest.Id;

    public string Name => Factory.Manifest.Name;

    public string Credit => string.Format(CultureInfo.CurrentCulture, Strings.SpriteSettings_AuthorFormat, Factory.Manifest.Author, Factory.Manifest.Version);
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
    private SpriteOption _selectedSprite;
    private SpriteSizeOption _selectedSize;
    private bool _voiceEnabled;

    public SpriteSettingsViewModel(IModelCatalog catalog, ISpriteCatalog sprites, IUiDispatcher dispatcher)
    {
        _catalog = catalog;
        Sprites = [.. sprites.All.Select(f => new SpriteOption(f))];
        if (Sprites.Count == 0)
            throw new InvalidOperationException("沒有任何已註冊的小精靈角色。");

        var saved = catalog.Current.Sprite;
        _selectedSprite = Find(saved.SpriteId);
        _selectedSize = SpriteSizeOption.All.First(o => o.Size == saved.Size);
        _voiceEnabled = saved.VoiceEnabled;
        RebuildStates();

        catalog.Changed += (_, settings) => dispatcher.Post(() => Reload(settings.Sprite));
    }

    public IReadOnlyList<SpriteOption> Sprites { get; }

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
            if (value is null || value.Id == _selectedSprite.Id)
                return;
            _selectedSprite = value;
            OnSelectedSpriteChanged();
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
