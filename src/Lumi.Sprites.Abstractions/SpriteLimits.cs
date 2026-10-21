using Avalonia;

namespace Lumi.Sprites.Abstractions;

/// <summary>角色框縮放後的結果。<see cref="Warnings"/> 有內容就表示角色超出上限被縮小了（殼寫進 log）。</summary>
/// <param name="Scale">相對於 manifest 宣告的框（換算到這個檔位之後）的縮放比，不超過 1。</param>
public sealed record SpriteFit(SpriteOptions Options, double Scale, IReadOnlyList<string> Warnings)
{
    public bool WasAdjusted => Warnings.Count > 0;
}

/// <summary>
/// 角色尺寸與比例的上限，由開發者在這裡定義，角色不能覆寫（<c>docs/sprite-dev-guide.md</c> 第 5 節）。
/// 不合規的角色會被自動縮放到上限內，不會撐大視窗。
/// </summary>
public static class SpriteLimits
{
    /// <summary>角色框寬除以高，必須介於這兩個值之間。</summary>
    public const double AspectMin = 0.5;

    public const double AspectMax = 2.0;

    /// <summary>最長邊上限＝尺寸檔位光球直徑的這個倍數。</summary>
    public const double LongSideFactor = 1.6;

    /// <summary>特效外擴上限（每邊，相對於角色框對應邊長）。</summary>
    public const double EffectMarginMax = 0.4;

    public const int VoiceMaxLength = 200;

    /// <summary>manifest 的框是以這個直徑（「中」檔）宣告的。</summary>
    public const double ReferenceDiameter = 80;

    public static IReadOnlyList<double> Tiers { get; } = [64, 80, 104];

    public static double DefaultTier => ReferenceDiameter;

    public static SpriteFit Fit(SpriteManifest manifest, double tierDiameter)
    {
        var warnings = new List<string>();
        var factor = tierDiameter / ReferenceDiameter;
        var width = manifest.Box.Width * factor;
        var height = manifest.Box.Height * factor;
        var scale = 1.0;

        if (width <= 0 || height <= 0)
        {
            warnings.Add($"角色「{manifest.Id}」的框大小無效（{manifest.Box.Width}×{manifest.Box.Height}），改用光球直徑的正方形。");
            width = height = tierDiameter;
        }

        // 長寬比：太寬就縮窄、太高就縮矮
        var aspect = width / height;
        if (aspect > AspectMax)
        {
            warnings.Add($"角色「{manifest.Id}」的長寬比 {aspect:0.##} 超過 {AspectMax:0.##}，已縮窄。");
            width = height * AspectMax;
        }
        else if (aspect < AspectMin)
        {
            warnings.Add($"角色「{manifest.Id}」的長寬比 {aspect:0.##} 低於 {AspectMin:0.##}，已縮矮。");
            height = width / AspectMin;
        }

        // 最長邊：整個等比例縮回上限
        var longSide = Math.Max(width, height);
        var longSideMax = tierDiameter * LongSideFactor;
        if (longSide > longSideMax)
        {
            warnings.Add($"角色「{manifest.Id}」的最長邊 {longSide:0.#} 超過上限 {longSideMax:0.#}，已縮小。");
            var shrink = longSideMax / longSide;
            width *= shrink;
            height *= shrink;
        }

        scale = Math.Min(width / (manifest.Box.Width * factor), height / (manifest.Box.Height * factor));
        if (double.IsNaN(scale) || scale > 1)
            scale = 1;

        var margin = manifest.EffectMargin;
        if (margin is < 0 or > EffectMarginMax || double.IsNaN(margin))
        {
            warnings.Add($"角色「{manifest.Id}」的特效外擴 {manifest.EffectMargin:0.##} 超出 0～{EffectMarginMax:0.##}，已限制。");
            margin = double.IsNaN(margin) ? 0 : Math.Clamp(margin, 0, EffectMarginMax);
        }

        var box = new Size(width, height);
        var outer = new Size(width * (1 + 2 * margin), height * (1 + 2 * margin));
        return new SpriteFit(new SpriteOptions(tierDiameter, box, outer), scale, warnings);
    }
}
