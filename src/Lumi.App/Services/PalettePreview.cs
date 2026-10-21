using System.Collections.Concurrent;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Lumi.Core.Models;

namespace Lumi.App.Services;

/// <summary>一套色票的預覽色：Accent／Hover／Soft／Text（Light 變體）。</summary>
public sealed record PalettePreviewColors(Color Accent, Color Hover, Color Soft, Color Text);

/// <summary>
/// 設定頁色票卡片的色塊預覽。色塊必須是「那套色票自己的顏色」，而不是目前主題的 token，
/// 所以這裡載入 <c>Themes/Palettes/{id}.axaml</c> 並讀它 Light 變體的 LumiAccentColor／LumiAccentHoverColor／
/// LumiAccentSoftColor／LumiTextColor。色碼只存在色票檔，這裡不複製；結果依色票 ID 快取。
/// </summary>
public static class PalettePreview
{
    private static readonly ConcurrentDictionary<string, PalettePreviewColors> Cache = new();

    public static PalettePreviewColors For(string paletteId) => Cache.GetOrAdd(ThemePalettes.Normalize(paletteId), Load);

    private static PalettePreviewColors Load(string id)
    {
        var include = new ResourceInclude(new Uri("avares://Lumi.App/"))
        {
            Source = new Uri($"avares://Lumi.App/Themes/Palettes/{id}.axaml"),
        };
        var palette = include.Loaded;

        Color Read(string key) =>
            palette.TryGetResource(key, ThemeVariant.Light, out var value) && value is Color color
                ? color
                : throw new InvalidOperationException($"色票 {id} 的 Light 變體沒有 {key}。");

        return new PalettePreviewColors(Read("LumiAccentColor"), Read("LumiAccentHoverColor"), Read("LumiAccentSoftColor"), Read("LumiTextColor"));
    }
}
