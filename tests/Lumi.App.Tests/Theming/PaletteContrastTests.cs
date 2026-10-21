using System.Globalization;
using System.Xml.Linq;
using Lumi.Core.Models;

namespace Lumi.App.Tests.Theming;

/// <summary>
/// 色票對比與完整性：直接讀 Themes/Palettes/*.axaml 與 Tokens.axaml 的原始色碼計算（不需要 Avalonia 執行環境）。
/// 文字一律 ≥ 4.5:1（ui-guide.html 第 1 節）；只當圖示或大面積的非文字元件 ≥ 3:1。
/// </summary>
public sealed class PaletteContrastTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>文字與它所在的底色。</summary>
    public static IEnumerable<object[]> TextPairs() => new[]
    {
        new object[] { "Text", "Background" }, new object[] { "Text", "Surface" }, new object[] { "Text", "Chrome" },
        new object[] { "Text", "SurfaceAlt" }, new object[] { "Text", "AccentSoft" }, new object[] { "Text", "UserBubble" },
        new object[] { "TextMuted", "Background" }, new object[] { "TextMuted", "Surface" }, new object[] { "TextMuted", "Chrome" },
        new object[] { "TextMuted", "SurfaceAlt" }, new object[] { "TextMuted", "AccentSoft" },
        new object[] { "TextOnSoft", "AccentSoft" },
        new object[] { "AccentText", "AccentSoft" }, new object[] { "AccentText", "Surface" }, new object[] { "AccentText", "Background" },
        new object[] { "UserBubbleText", "UserBubble" }, new object[] { "PrimaryButtonText", "PrimaryButton" },
    };

    /// <summary>語意色（Tokens.axaml，不隨主題變）在它會出現的底色上的文字。</summary>
    public static IEnumerable<object[]> SemanticTextPairs() => new[]
    {
        new object[] { "Info", "Surface" }, new object[] { "Success", "Surface" }, new object[] { "Danger", "Surface" },
        new object[] { "Danger", "DangerBackground" }, new object[] { "WarningText", "WarningBackground" },
    };

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void PaletteText_MeetsWcagAa_InEveryTheme(string text, string background)
    {
        foreach (var (theme, variant) in AllPalettes())
        {
            var ratio = Contrast(theme.Colors[variant], text, background);
            Assert.True(ratio >= 4.5, $"{theme.Id} {variant}：{text} 在 {background} 上只有 {ratio:0.00}:1");
        }
    }

    [Fact]
    public void AccentIcon_OnAccent_MeetsNonTextThreshold_InEveryTheme()
    {
        // 送出鈕的箭頭是圖示，不是文字：WCAG 非文字對比 3:1
        foreach (var (theme, variant) in AllPalettes())
        {
            var ratio = Contrast(theme.Colors[variant], "OnAccent", "Accent");
            Assert.True(ratio >= 3.0, $"{theme.Id} {variant}：OnAccent 在 Accent 上只有 {ratio:0.00}:1");
        }
    }

    [Theory]
    [InlineData("SuccessDot")]
    [InlineData("WarningDot")]
    [InlineData("DangerDot")]
    public void StatusDots_AreFixedColors_AndMeetNonTextContrastOnEverySurface(string dot)
    {
        // 膠囊的狀態點是非文字元件：WCAG 3:1。狀態另外用文字表達（Tooltip），不只靠圓點。
        // 已知例外：ui-guide.html 指定的 Success #55A981、Warning #D99A3E 在淺色 Surface／Chrome 上只有 2.35–2.85:1
        // （色碼照原則，不調整）。這兩個組合只守住下限 2.3:1 防止再變差，並列在人工驗收（plan/12 的 UB-2 項）。
        var tokens = ReadColors(Path.Combine(ThemesDir, "Tokens.axaml"));
        Assert.Equal(tokens["Light"][dot], tokens["Dark"][dot]);   // 不隨明暗變
        foreach (var (theme, variant) in AllPalettes())
        {
            var colors = new Dictionary<string, string>(theme.Colors[variant]);
            foreach (var (key, value) in tokens[variant])
                colors[key] = value;
            foreach (var surface in new[] { "Surface", "Chrome" })
            {
                var ratio = Contrast(colors, dot, surface);
                var knownLow = variant == "Light" && dot is "SuccessDot" or "WarningDot";
                var minimum = knownLow ? 2.3 : 3.0;
                Assert.True(ratio >= minimum, $"{theme.Id} {variant}：{dot} 在 {surface} 上只有 {ratio:0.00}:1（需要 {minimum}:1）");
            }
        }
    }

    [Theory]
    [MemberData(nameof(SemanticTextPairs))]
    public void SemanticText_MeetsWcagAa_OnEveryTheme(string text, string background)
    {
        // 語意色（Tokens）與各主題的底色（Palettes）合併後計算：Surface 之類的底色來自色票
        var tokens = ReadColors(Path.Combine(ThemesDir, "Tokens.axaml"));
        foreach (var (theme, variant) in AllPalettes())
        {
            var colors = new Dictionary<string, string>(theme.Colors[variant]);
            foreach (var (key, value) in tokens[variant])
                colors[key] = value;
            var ratio = Contrast(colors, text, background);
            Assert.True(ratio >= 4.5, $"{theme.Id} {variant}：{text} 在 {background} 上只有 {ratio:0.00}:1");
        }
    }

    [Fact]
    public void EveryPalette_DefinesTheSameColors_InLightAndDark()
    {
        var expected = AllPalettes().First().Item1.Colors["Light"].Keys.OrderBy(k => k).ToArray();
        foreach (var (theme, variant) in AllPalettes())
            Assert.True(expected.SequenceEqual(theme.Colors[variant].Keys.OrderBy(k => k)), $"{theme.Id} {variant} 的色碼與 sage 不一致");
    }

    [Fact]
    public void PaletteFiles_MatchThemePaletteIds()
    {
        var files = Directory.GetFiles(Path.Combine(ThemesDir, "Palettes"), "*.axaml")
            .Select(Path.GetFileNameWithoutExtension).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(ThemePalettes.All.OrderBy(x => x, StringComparer.Ordinal), files);
    }

    private static Dictionary<string, Dictionary<string, string>> ReadColors(string path)
    {
        var doc = XDocument.Load(path);
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var dict in doc.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(X + "Key") is { } key && key.Value is "Light" or "Dark"))
        {
            var colors = dict.Elements().Where(e => e.Name.LocalName == "Color")
                .ToDictionary(e => e.Attribute(X + "Key")!.Value.Replace("Lumi", "").Replace("Color", ""), e => e.Value.Trim());
            result[dict.Attribute(X + "Key")!.Value] = colors;
        }
        return result;
    }

    private static double Contrast(IReadOnlyDictionary<string, string> colors, string a, string b)
    {
        var la = Luminance(colors[a]);
        var lb = Luminance(colors[b]);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string hex)
    {
        var h = hex.TrimStart('#');
        double Channel(int start)
        {
            var c = int.Parse(h.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static string ThemesDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lumi.sln")))
                dir = dir.Parent;
            return Path.Combine(dir!.FullName, "src", "Lumi.App", "Themes");
        }
    }

    private sealed record Theme(string Id, Dictionary<string, Dictionary<string, string>> Colors);

    private static IEnumerable<(Theme, string)> AllPalettes()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(ThemesDir, "Palettes"), "*.axaml").OrderBy(p => p, StringComparer.Ordinal))
        {
            var theme = new Theme(Path.GetFileNameWithoutExtension(path), ReadColors(path));
            yield return (theme, "Light");
            yield return (theme, "Dark");
        }
    }
}
